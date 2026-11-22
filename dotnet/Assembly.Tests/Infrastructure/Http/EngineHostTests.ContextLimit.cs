using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class EngineHostTests
{
    [Theory]
    [InlineData("desktop")]
    [InlineData("remote")]
    public async Task Context_rows_over_the_socket_use_each_provider_s_effective_auto_compact_limit(string host)
    {
        var home = Directory.CreateTempSubdirectory("assembly-context-limit-").FullName;
        var codexHome = Path.Combine(home, "codex-home");
        var codexEmptyHome = Path.Combine(home, "codex-empty");
        var claudeHome = Path.Combine(home, "claude-home");
        var claudeEmptyHome = Path.Combine(home, "claude-empty");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(codexEmptyHome);
        Directory.CreateDirectory(claudeHome);
        Directory.CreateDirectory(claudeEmptyHome);
        await File.WriteAllTextAsync(Path.Combine(codexHome, "config.toml"), """
            model_context_window = 250000
            model_auto_compact_token_limit = 200000
            """);
        await File.WriteAllTextAsync(Path.Combine(claudeHome, "settings.json"), """
            {
              "autoCompactWindow": 400000,
              "autoCompactEnabled": true
            }
            """);
        var launcher = new FakeLauncher
        {
            Agents =
            [
                new("codex", "Codex", "codex-acp", [], new Dictionary<string, string> { ["CODEX_HOME"] = codexHome }, VendorFamily: "codex"),
                new("codex-default", "Codex Default", "codex-acp", [], new Dictionary<string, string> { ["CODEX_HOME"] = codexEmptyHome }, VendorFamily: "codex"),
                new("claude", "Claude Code", "claude-agent-acp", [], new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = claudeHome }, VendorFamily: "claude"),
                new("claude-default", "Claude Default", "claude-agent-acp", [], new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = claudeEmptyHome }, VendorFamily: "claude"),
            ],
        };
        try
        {
            await using var running = await Host.Start(host, home, null, launcher: launcher);
            using var socket = await running.Open();
            using var bulk = await running.Open(bulk: true);

            // 1. Codex: raw model window 237,500 is clamped to the configured model_auto_compact_token_limit (200,000).
            await Command(socket, "codex-session", new StartSession("codex", "model", home, "Codex", null, null, null));
            await running.WaitState("sessions", "codex-session", "idle");
            using var codexRows = await running.Open();
            await SubscribeRows(codexRows, "codex-session");

            var codexAgent = await OpenHostTurn(running, socket, launcher, "codex-session", "codex-turn-1");
            await codexAgent.UpdateAsync("""{"sessionUpdate":"usage_update","used":45545,"size":237500}""");
            await FinishHostTurn(codexAgent);

            var codexPushed = await ReadContextRowsUntilTurnEnd(codexRows);
            Assert.Equal([(45545L, 200000L)], codexPushed);
            Assert.Equal([(45545L, 200000L)], await PageContextRows(bulk, "codex-session"));

            // 2. Claude: turn-end result.modelUsage (1,000,000) never widens the effective autoCompactWindow (400,000),
            // and a second session receiving 1,000,000 from a warm adapter cache is clamped to 400,000 from the start.
            await Command(socket, "claude-session", new StartSession("claude", "model", home, "Claude", null, null, null));
            await running.WaitState("sessions", "claude-session", "idle");
            using var claudeRows = await running.Open();
            await SubscribeRows(claudeRows, "claude-session");

            var claudeAgent = await OpenHostTurn(running, socket, launcher, "claude-session", "claude-turn-1");
            await claudeAgent.UpdateAsync("""{"sessionUpdate":"usage_update","used":46848,"size":400000,"_meta":{"_claude/model":"claude-opus-5-5"}}""");
            await claudeAgent.UpdateAsync("""{"sessionUpdate":"usage_update","used":47516,"size":1000000,"cost":{"amount":0.21,"currency":"USD"},"_meta":{"_claude/model":"claude-opus-5-5"}}""");
            await FinishHostTurn(claudeAgent);

            var claudePushed = await ReadContextRowsUntilTurnEnd(claudeRows);
            Assert.Equal([(46848L, 400000L), (47516L, 400000L)], claudePushed);
            Assert.Equal([(47516L, 400000L)], await PageContextRows(bulk, "claude-session"));

            // 3. Claude warm adapter cache: a second session on the already-running adapter receiving 1,000,000 first still clamps to 400,000.
            await Command(socket, "claude-warm-session", new StartSession("claude", "model", home, "Claude Warm", null, null, null));
            await running.WaitState("sessions", "claude-warm-session", "idle");
            using var claudeWarmRows = await running.Open();
            await SubscribeRows(claudeWarmRows, "claude-warm-session");

            await Command(socket, "claude-warm-turn", new SendMessage(null, "claude-warm-session", "Work", "prompt"));
            await claudeAgent.OpenAsync("acp-warm", configOptions: """[{"id":"model","category":"model","type":"select","currentValue":"model","options":[{"value":"model","name":"Model"}]}]""");
            var warmPrompt = await claudeAgent.ReadAsync("session/prompt");
            await claudeAgent.InsertAsync(warmPrompt, "claude-warm-turn-provider");
            await running.WaitState("messages", "claude-warm-turn", "next_turn");
            await claudeAgent.UpdateAsync("""{"sessionUpdate":"usage_update","used":47620,"size":1000000,"_meta":{"_claude/model":"claude-opus-5-5"}}""", "acp-warm");
            await claudeAgent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", "acp-warm");
            Assert.Equal("acp-warm", await claudeAgent.CloseAsync());

            Assert.Equal([(47620L, 400000L)], await ReadContextRowsUntilTurnEnd(claudeWarmRows));
            Assert.Equal([(47620L, 400000L)], await PageContextRows(bulk, "claude-warm-session"));

            // 4. Claude without settings.json: once an effective window (400,000) is observed, turn-end 1,000,000 still never widens it.
            await Command(socket, "claude-default-session", new StartSession("claude-default", "model", home, "Claude Default", null, null, null));
            await running.WaitState("sessions", "claude-default-session", "idle");
            using var claudeDefaultRows = await running.Open();
            await SubscribeRows(claudeDefaultRows, "claude-default-session");

            var claudeDefaultAgent = await OpenHostTurn(running, socket, launcher, "claude-default-session", "claude-default-turn");
            await claudeDefaultAgent.UpdateAsync("""{"sessionUpdate":"usage_update","used":46000,"size":400000}""");
            await claudeDefaultAgent.UpdateAsync("""{"sessionUpdate":"usage_update","used":47000,"size":1000000,"_meta":{"_claude/model":"claude-opus-5-5"}}""");
            await FinishHostTurn(claudeDefaultAgent);

            Assert.Equal([(46000L, 400000L), (47000L, 400000L)], await ReadContextRowsUntilTurnEnd(claudeDefaultRows));
            Assert.Equal([(47000L, 400000L)], await PageContextRows(bulk, "claude-default-session"));

            // 5. Codex without model_auto_compact_token_limit: keeps the adapter's reported effective window (237,500).
            await Command(socket, "codex-default-session", new StartSession("codex-default", "model", home, "Codex Default", null, null, null));
            await running.WaitState("sessions", "codex-default-session", "idle");
            using var codexDefaultRows = await running.Open();
            await SubscribeRows(codexDefaultRows, "codex-default-session");

            var codexDefaultAgent = await OpenHostTurn(running, socket, launcher, "codex-default-session", "codex-default-turn");
            await codexDefaultAgent.UpdateAsync("""{"sessionUpdate":"usage_update","used":45545,"size":237500}""");
            await FinishHostTurn(codexDefaultAgent);

            Assert.Equal([(45545L, 237500L)], await ReadContextRowsUntilTurnEnd(codexDefaultRows));
            Assert.Equal([(45545L, 237500L)], await PageContextRows(bulk, "codex-default-session"));
        }
        finally { Directory.Delete(home, true); }
    }

    private static async Task SubscribeRows(ClientWebSocket socket, string session)
    {
        await Send(socket, new { t = "sub", id = 11, topic = "rows", key = session });
        var state = await Read(socket);
        Assert.Equal("state", state.GetProperty("t").GetString());
    }

    private static async Task<List<(long Used, long Size)>> ReadContextRowsUntilTurnEnd(ClientWebSocket socket)
    {
        List<(long Used, long Size)> contexts = [];
        while (true)
        {
            var frame = await Read(socket);
            if (frame.GetProperty("t").GetString() != "rows") continue;
            var row = frame.GetProperty("row");
            var kind = row.GetProperty("kind").GetString();
            var payload = row.GetProperty("payload");
            if (kind == "context")
                contexts.Add((payload.GetProperty("used").GetInt64(), payload.GetProperty("size").GetInt64()));
            if (kind == "turn" && payload.GetProperty("state").GetString() != "started")
                return contexts;
        }
    }

    private static async Task<List<(long Used, long Size)>> PageContextRows(ClientWebSocket bulk, string session)
    {
        await Send(bulk, new { t = "call", id = 12, feature = "rows", method = "page", args = new { session, before = long.MaxValue, limit = 100 } });
        var frame = await Read(bulk);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        return frame.GetProperty("result").GetProperty("rows").EnumerateArray()
            .Where(row => row.GetProperty("kind").GetString() == "context")
            .Select(row =>
            {
                var payload = row.GetProperty("payload");
                return (payload.GetProperty("used").GetInt64(), payload.GetProperty("size").GetInt64());
            })
            .ToList();
    }
}
