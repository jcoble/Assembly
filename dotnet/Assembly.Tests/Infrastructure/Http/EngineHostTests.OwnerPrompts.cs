using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Sessions;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class EngineHostTests
{
    [Theory]
    [InlineData("desktop", false)]
    [InlineData("remote", false)]
    [InlineData("desktop", true)]
    [InlineData("remote", true)]
    public async Task Public_send_commits_owner_ack_before_provider_insertion_and_replays_it(string host, bool legacy)
    {
        var home = Directory.CreateTempSubdirectory("assembly-owner-send-").FullName;
        var launcher = new FakeLauncher();
        try
        {
            await using var running = await Host.Start(host, home, null, launcher: launcher);
            using var socket = await running.Open();
            await Command(socket, "owner", new StartSession("codex", null, home, "Owner", null, null, null));
            await running.WaitState("sessions", "owner", "idle");
            using var rows = await ApiTests.OpenRowsAsync(running.Address, "engine-token", "owner", 0);
            const string text = "Exact owner text\nsecond line";
            var accepted = await OwnerSend(socket, text, legacy ? null : "owner-C");
            var client = accepted.GetProperty("clientId").GetString()!;
            Assert.Equal(legacy ? 32 : 7, client.Length);
            Assert.Equal("owner", accepted.GetProperty("sessionId").GetString());
            Assert.Equal("prompt/owner/" + client, accepted.GetProperty("messageId").GetString());
            Assert.Equal("queued", accepted.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, accepted.GetProperty("runId").ValueKind);
            Assert.True(accepted.GetProperty("messageVersion").GetInt64() > 0);
            Assert.True(accepted.GetProperty("seq").GetInt64() > 0);
            Assert.Equal(0, accepted.GetProperty("epoch").GetInt64());
            Assert.Equal(accepted.GetRawText(), (await OwnerSend(socket, text, client)).GetRawText());
            Assert.Equal(1, ((Assembly.Engine.Hosting.IEngineRuntime)running.Runtime).Read(scope => scope.QueryFirst(
                "SELECT COUNT(*) FROM engine_message WHERE id=$id", r => r.Long(0),
                new SqlArg("$id", "prompt/owner/" + client)), default));
            // The provider has not even answered initialize: a committed queue ACK is independent of insertion.
            Assert.Equal(0, ((Assembly.Engine.Hosting.IEngineRuntime)running.Runtime).Read(scope => scope.QueryFirst(
                "SELECT COUNT(*) FROM rows WHERE session_id='owner' AND kind='user' AND json_extract(payload,'$.acknowledgment.kind')='prompt_inserted'",
                r => r.Long(0)), default));
            await Send(socket, new
            {
                t = "call",
                id = 2,
                feature = "send",
                method = "send",
                args = new { session = "owner", text = "Changed", clientId = client }
            });
            var conflict = await Read(socket);
            Assert.Equal("error", conflict.GetProperty("t").GetString());
            Assert.Equal("failed", conflict.GetProperty("code").GetString());
            Assert.Equal("client_id_conflict", conflict.GetProperty("message").GetString());

            var agent = await launcher.NextAsync();
            await agent.StartAsync();
            var prompt = await agent.ReadAsync("session/prompt");
            Assert.Equal(text, prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
            await agent.InsertAsync(prompt, "provider-user");
            await running.WaitState("messages", "prompt/owner/" + client, "next_turn");
            using var bulk = await running.Open(bulk: true);
            var message = await Entity(bulk, "message", "prompt/owner/" + client);
            Assert.Equal(client, message.GetProperty("clientId").GetString());
            Assert.Equal("owner", message.GetProperty("toSessionId").GetString());
            Assert.Equal("provider-user", message.GetProperty("providerTurnId").GetString());
            Assert.Equal(JsonValueKind.Null, message.GetProperty("runId").ValueKind);
            JsonElement user;
            do { user = (await Read(rows)).GetProperty("row").Clone(); }
            while (user.GetProperty("kind").GetString() != "user");
            Assert.Equal(client, user.GetProperty("payload").GetProperty("clientId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(user.GetProperty("runId").GetString()));
            Assert.Equal(accepted.GetRawText(), (await OwnerSend(socket, text, client)).GetRawText());
            await FinishHostTurn(agent);
            await running.WaitState("sessions", "owner", "idle");
            Assert.Equal(accepted.GetRawText(), (await OwnerSend(socket, text, client)).GetRawText());
            Assert.Equal(1, launcher.Launches);
        }
        finally { Directory.Delete(home, true); }
    }

    private static async Task<JsonElement> OwnerSend(ClientWebSocket socket, string text, string? client)
    {
        await Send(socket, new
        {
            t = "call",
            id = 2,
            feature = "send",
            method = "send",
            args = new { session = "owner", text, clientId = client, attachmentIds = Array.Empty<string>(), epoch = (long?)null }
        });
        var frame = await Read(socket);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        return frame.GetProperty("result").Clone();
    }
}
