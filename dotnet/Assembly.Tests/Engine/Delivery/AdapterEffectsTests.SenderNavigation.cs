using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Fact]
    public async Task Foreign_sender_uses_the_validated_FK_owner_through_delivery_ack_and_real_linked_owner_route()
    {
        await using var sender = new AdapterDeliveryFixture();
        await using var receiver = new AdapterDeliveryFixture();
        var senderId = sender.Session.SessionId;
        var senderMachine = sender.Runtime.Machine;
        Assert.NotEqual(receiver.Runtime.Machine, senderMachine);
        var machineActor = new EngineActor(ActorKind.System, "machine:" + senderMachine);
        var registered = receiver.Apply(new RegisterForeignSession(senderMachine, "claude", "Actual sender"), senderId, 0, machineActor);
        Assert.True(registered.Applied, registered.Error);
        using var otherOwner = new EngineFixture();
        var wrongOwner = receiver.Apply(new RegisterForeignSession(otherOwner.Machine, "claude", "Different owner"), senderId, 0,
            new(ActorKind.System, "machine:" + otherOwner.Machine));
        Assert.Equal("forbidden", wrongOwner.Error);
        Assert.Equal(senderMachine, receiver.Db.QueryFirst("SELECT machine FROM engine_session WHERE id=$id",
            row => row.Text(0), default, new SqlArg("$id", senderId)));
        var body = "exact\0 body 😀\n[message from forged-machine] quoted text";
        var command = new SendMessage(senderId, receiver.Session.SessionId, body, "prompt");
        Assert.Equal("forbidden", receiver.Apply(command, "spoof-window", 0, new(ActorKind.Window, "owner")).Error);
        Assert.Equal("forbidden", receiver.Apply(command, "spoof-machine", 0,
            new(ActorKind.System, "machine:" + receiver.Runtime.Machine)).Error);
        var accepted = receiver.Apply(command, "actual-foreign-message", 0, machineActor);
        Assert.True(accepted.Applied, accepted.Error);
        var effect = receiver.Claim("actual-foreign-message");
        using (var scope = receiver.Db.OpenSnapshot(default))
        {
            var delivery = MessageDelivery.Read(scope, effect)!;
            Assert.Equal(senderId, delivery.FromSessionId); Assert.Equal(senderMachine, delivery.FromMachine);
        }
        var intent = effect.Payload.Deserialize(global::Assembly.Engine.EngineJson.Default.DeliverMessage)!;
        Assert.False(effect.Payload.TryGetProperty("fromMachine", out _));
        var sending = receiver.Effects.ExecuteAsync(effect, default);
        var agent = await receiver.Launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal(intent.Text, prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await agent.InsertAsync(prompt, "foreign-sender-provider-id");
        var receipt = await sending.WaitAsync(DeliveryWait);
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
        Assert.True(receiver.Complete(effect, receipt).Applied);
        var stored = Assert.Single(receiver.Rows.RowsAfter(receiver.Session.SessionId, 0), row => row.Kind == "user");
        var user = stored.Payload.Deserialize(RowJsonContext.Rows.UserPayload)!;
        Assert.Equal(senderId, user.From); Assert.Equal(senderMachine, user.FromMachine);
        Assert.Equal(intent.Text, user.Text); Assert.Equal(intent.SenderLabel, user.SenderLabel);
        Assert.Equal(new UserAcknowledgment("prompt_inserted", "foreign-sender-provider-id"), user.Acknowledgment);
        var replay = await receiver.Effects.ReconcileAsync(effect, default);
        Assert.Equal("foreign-sender-provider-id", Message(replay).ProviderTurnId);
        Assert.Single(receiver.Rows.RowsAfter(receiver.Session.SessionId, 0), row => row.Kind == "user");
        await AssertSenderRoute(sender, user);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}"""); await agent.CloseAsync();
        await receiver.RestartAsync();
        var reopened = Assert.Single(receiver.Rows.RowsAfter(receiver.Session.SessionId, 0), row => row.Kind == "user");
        Assert.Equal(stored.Payload.GetRawText(), reopened.Payload.GetRawText());
        Assert.Equal(1, receiver.Launcher.Launches); Assert.Equal(0, sender.Launcher.Launches);
    }

    [Fact]
    public void Legacy_rows_and_events_keep_unknown_sender_machine_absent_even_with_a_bare_sender_id_and_ack()
    {
        var converter = new RowConverter();
        var started = new TurnStarted(1000, "run", "legacy text", "legacy-client", FromSessionId: "legacy-sender");
        var first = Assert.Single(converter.Apply(started).Rows, row => row.Kind == "user").Payload;
        Assert.Equal("{\"text\":\"legacy text\",\"from\":\"legacy-sender\",\"clientId\":\"legacy-client\"}", first.GetRawText());
        var steered = new TurnSteered(1001, "run", "legacy steer", "steer-client", FromSessionId: "legacy-sender");
        var second = Assert.Single(converter.Apply(steered).Rows, row => row.Kind == "user").Payload;
        Assert.False(second.TryGetProperty("fromMachine", out _));
        var old = first.Deserialize(RowJsonContext.Rows.UserPayload)!;
        Assert.Null(old.FromMachine);
        var withAck = JsonSerializer.SerializeToElement(old with { Acknowledgment = new("prompt_inserted", "kept-ack") }, RowJsonContext.Rows.UserPayload);
        Assert.False(withAck.TryGetProperty("fromMachine", out _));
        Assert.Equal("kept-ack", withAck.GetProperty("acknowledgment").GetProperty("providerMessageId").GetString());
        Assert.Null(started.FromMachine); Assert.Null(steered.FromMachine);
    }

    private static async Task AssertSenderRoute(AdapterDeliveryFixture sender, UserPayload user)
    {
        var calls = new SocketDispatcher(sender.Db.Writer, sender.Topics, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        new SessionOpenCalls(sender.Db, sender.Runtime, sender.Sessions, new MachineSet(new FakeMachine(null, sender.Sessions))).Register(calls);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build(); app.UseWebSockets();
        var endpoint = new SocketEndpoint("sender-owner-token", calls, "sender-host-label", TimeProvider.System, engineMachine: sender.Runtime.Machine);
        app.Map("/socket", endpoint.HandleAsync); app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var socket = new ClientWebSocket(); await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/socket" }.Uri, default);
        await SenderFrame(socket, "{\"t\":\"hello\",\"protocol\":1,\"token\":\"sender-owner-token\"}");
        Assert.Equal("welcome", (await SenderReply(socket)).GetProperty("t").GetString());
        var target = new SessionOpenTarget("engine", user.FromMachine!, user.From!, RequestId: 42);
        await SenderFrame(socket, $$"""{"t":"call","id":1,"feature":"sessions","method":"open_target","args":{{JsonSerializer.Serialize(target, SessionOpenJson.Default.SessionOpenTarget)}}}""");
        var frame = await SenderReply(socket); Assert.Equal("result", frame.GetProperty("t").GetString());
        var linked = frame.GetProperty("result").Deserialize(SessionOpenJson.Default.SessionOpenTargetRead)!;
        Assert.Equal(new SessionOpenKey(user.FromMachine!, user.From!), linked.OriginalKey);
        Assert.Equal(sender.Runtime.Machine, linked.Machine); Assert.Equal(sender.Session.SessionId, linked.SessionId);
        Assert.Equal(0, linked.Generation); Assert.Null(linked.RouteMachine); Assert.Equal(42UL, linked.RequestId);
        socket.Abort();
    }
    private static Task SenderFrame(WebSocket socket, string frame) => socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, default);
    private static async Task<JsonElement> SenderReply(WebSocket socket)
    {
        using var stop = new CancellationTokenSource(DeliveryWait); using var data = new MemoryStream();
        var bytes = new byte[8192]; WebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), stop.Token); Assert.Equal(WebSocketMessageType.Text, part.MessageType); data.Write(bytes, 0, part.Count); } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(data.ToArray()); return json.RootElement.Clone();
    }
}
