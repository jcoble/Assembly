using Assembly.Tests.Engine;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Messages;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Machines;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Fact]
    public async Task Actual_registered_socket_Engine_send_reaches_worker_host_provider_and_atomic_ack_once()
    {
        await using var f = new AdapterDeliveryFixture(worker: true);
        var attachments = new AttachmentStore(Path.GetTempPath()); var local = new LocalMachine(f.Hub, f.Sessions, f.Hosts, attachments);
        await using var app = ApiServer.Build(new(f.Hub, f.Sessions, new MachineSet(local), attachments, "isolated-token",
            Writer: f.Db.Writer, Topics: f.Topics, Features: [new EngineCalls(f.Runtime, f.Time)], Engine: f.Runtime), 0, out _);
        await app.StartAsync(); var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var socket = new ClientWebSocket(); await socket.ConnectAsync(new($"ws://{address.Authority}/socket"), default);
        await SocketSend(socket, """{"t":"hello","token":"isolated-token","protocol":1}"""); Assert.Equal("welcome", (await SocketRead(socket)).GetProperty("t").GetString());
        var command = new WindowCommand(Guid.NewGuid(), "registered-message", 0, 0, new SendMessage(null, f.Session.SessionId, "registered body", "prompt"));
        var args = JsonSerializer.Serialize(command, EngineSocketJson.Default.WindowCommand);
        await SocketSend(socket, $$"""{"t":"call","id":1,"feature":"engine","method":"command","args":{{args}}}""");
        Assert.True((await SocketRead(socket)).GetProperty("result").GetProperty("applied").GetBoolean());
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal(MessageText.Prompt("Jesse", "registered body"), prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await agent.InsertAsync(prompt, "registered-provider"); await f.StateAsync("engine_message", "registered-message", "next_turn");
        using (var snapshot = f.Db.OpenSnapshot(default))
        {
            var row = f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, "registered-message")!;
            Assert.Null(row.From); Assert.Equal("Jesse", row.SenderLabel); Assert.Equal("registered-provider", row.Acknowledgment!.ProviderMessageId);
        }
        await SocketSend(socket, $$"""{"t":"call","id":2,"feature":"engine","method":"command","args":{{args}}}""");
        Assert.True((await SocketRead(socket)).GetProperty("result").GetProperty("applied").GetBoolean());
        Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        await app.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_registered_legacy_http_send_waits_for_insertion_and_keeps_response_shape(bool legacyFrame)
    {
        await using var f = new AdapterDeliveryFixture(); var attachments = new AttachmentStore(Path.GetTempPath());
        var local = new LocalMachine(f.Hub, f.Sessions, f.Hosts, attachments);
        await using var app = ApiServer.Build(new(f.Hub, f.Sessions, new MachineSet(local), attachments, "isolated-token"), 0, out _);
        await app.StartAsync(); using var http = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "isolated-token");
        var text = legacyFrame ? Assembly.Application.Features.Chat.Helpers.MessageFrame.Of("old-message", f.Session, "HTTP body") : "HTTP body";
        var body = JsonSerializer.Serialize(new { text, clientId = "http-client" });
        var sending = http.PostAsync($"/sessions/{f.Session.SessionId}/send", new StringContent(body, Encoding.UTF8, "application/json"));
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt"); Assert.False(sending.IsCompleted);
        var pending = Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user");
        Assert.Equal(text, prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal(legacyFrame ? f.Session.SessionId : null, pending.Payload.TryGetProperty("from", out var from) ? from.GetString() : null);
        Assert.False(pending.Payload.TryGetProperty("senderLabel", out _));
        await agent.InsertAsync(prompt, "http-provider"); using var response = await sending.WaitAsync(DeliveryWait); response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(pending.RunId, result.RootElement.GetProperty("runId").GetString());
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        using var duplicate = await http.PostAsync($"/sessions/{f.Session.SessionId}/send", new StringContent(body, Encoding.UTF8, "application/json"));
        duplicate.EnsureSuccessStatusCode(); Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user"); Assert.Equal(1, f.Launcher.Launches);
        await app.StopAsync();
    }

    private static Task SocketSend(ClientWebSocket socket, string text) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> SocketRead(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(DeliveryWait); using var bytes = new MemoryStream(); var buffer = new byte[65536]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); bytes.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(bytes.ToArray()); return document.RootElement.Clone();
    }
}
