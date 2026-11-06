using System.Net.WebSockets;
using Assembly.Tests.Infrastructure.Http;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Messages;
using Assembly.Infrastructure.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Assembly.Tests.Infrastructure.Engine;

public sealed partial class OwnerPromptDoorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(7L)]
    public async Task Actual_owner_http_reopen_reconstructs_original_with_engine_identity_and_authorization(long? epoch)
    {
        await using var h = new Harness();
        var session = h.Sessions.Create("codex", "repo"); var id = session.SessionId;
        h.Fixture.SeedSession(id, state: "running", epoch: 7);
        var text = "\0" + new string('a', 2 * 1024 * 1024) + "😀\0end";
        var reference = h.Attachments.PathOf(id, $"{Guid.NewGuid()}.png");
        var request = new OwnerPromptRequest(id, text, "C", [reference], epoch);
        var accepted = h.Fixture.Db.Writer.Write("send/send", await h.Door.PrepareOwnerPromptAsync(request, default));
        await h.Reopen();
        await using var app = ApiServer.Build(h.SocketServices, 0, out _);
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        await OwnerDeniedAsync(address);
        using var socket = await ApiTests.OpenOwnerSocketAsync(address, "owner-token");
        using var bulk = await ApiTests.OpenOwnerSocketAsync(address, "owner-token", "/socket/bulk");
        var pageResult = ApiTests.Result(await OwnerCallAsync(socket, "owner_prompts", new { session = id }));
        var pageBytes = Encoding.UTF8.GetBytes(pageResult.GetRawText());
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { t = "result", id = 1, result = pageResult })) <= 65536);
        var page = pageResult.Deserialize(SessionsJson.Default.OwnerPromptPage)!;
        Assert.Equal(h.Fixture.Machine, page.EngineMachine); Assert.NotEqual("paired-phone-id", page.EngineMachine);
        var manifest = Assert.Single(page.Items); Assert.False(manifest.TextComplete); Assert.Equal(epoch, manifest.TargetEpoch);
        Assert.Equal(pageBytes.Length, JsonReply.Encode(json => JsonSerializer.Serialize(json, page, SessionsJson.Default.OwnerPromptPage)).Length);
        async Task<string> Part(string part)
        {
            using var all = new MemoryStream(); long offset = 0;
            while (true)
            {
                var chunk = ApiTests.Result(await OwnerCallAsync(bulk, "owner_prompt_chunk",
                    new { session = id, clientId = "C", part, offset, contentSha256 = manifest.ContentSha256, machine = page.EngineMachine }))
                    .Deserialize(SessionsJson.Default.OwnerPromptChunk)!;
                all.Write(chunk.Data);
                if (chunk.NextOffset is not { } next) break;
                offset = next;
            }
            return new UTF8Encoding(false, true).GetString(all.ToArray());
        }
        var exactText = await Part("text"); var references = JsonSerializer.Deserialize(await Part("attachments"), SessionsJson.Default.IReadOnlyListString)!;
        Assert.Equal(text, exactText); Assert.Equal(request.AttachmentIds, references);
        var full = OwnerPromptFingerprint.Prepare(page.EngineMachine, new(id, exactText, manifest.ClientId, references, manifest.TargetEpoch), default);
        Assert.Equal(manifest.ContentSha256, full.ContentSha256);
        Assert.NotEqual(full.ContentSha256, OwnerPromptFingerprint.Prepare("paired-phone-id", new(id, exactText, manifest.ClientId, references, manifest.TargetEpoch), default).ContentSha256);
        Assert.Equal(accepted, h.Fixture.Db.Writer.Write("send/send", await h.Door.PrepareOwnerPromptAsync(new(id, exactText, "C", references, manifest.TargetEpoch), default)));
        await OwnerErrorAsync(socket, "owner_prompts", new { session = id, machine = "paired-phone-id" }, "not_found");
        await OwnerErrorAsync(bulk, "owner_prompt_chunk", new { session = id, clientId = "other", part = "text", offset = 0, contentSha256 = manifest.ContentSha256 }, "not_found");
        await OwnerErrorAsync(bulk, "owner_prompt_chunk", new { session = id, clientId = "C", part = "text", offset = 0, contentSha256 = new string('a', 64) }, "failed");
        await OwnerErrorAsync(socket, "owner_prompts", new { session = id, limit = "invalid" }, "bad_args");
        Assert.Empty(h.Machine.Asked); Assert.Equal(1, h.Fixture.Count("engine_message"));
    }
    private static Task<JsonElement> OwnerCallAsync(ClientWebSocket socket, string method, object args) =>
        ApiTests.SocketRequestAsync(socket, 1, "engine", method, JsonSerializer.Serialize(args));

    private static async Task OwnerErrorAsync(ClientWebSocket socket, string method, object args, string code)
    {
        var error = await OwnerCallAsync(socket, method, args);
        Assert.Equal("error", error.GetProperty("t").GetString());
        Assert.Equal(code, error.GetProperty("code").GetString());
    }

    private static async Task OwnerDeniedAsync(Uri address)
    {
        using var socket = new ClientWebSocket();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri($"ws://{address.Authority}/socket"), deadline.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"t":"hello","protocol":1}""").AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), deadline.Token);
        Assert.Equal(WebSocketMessageType.Close, received.MessageType);
        Assert.Equal((WebSocketCloseStatus)4401, received.CloseStatus);
    }

}
