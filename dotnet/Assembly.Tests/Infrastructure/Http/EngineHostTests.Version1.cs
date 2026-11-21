using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Engine;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Sessions;
using Assembly.Tests.Acp;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class EngineHostTests
{
    private const string RecordedSession = "5b536aba-06b5-442a-8328-7eb507496cea";
    private const string HostModel = """[{"id":"model","category":"model","type":"select","currentValue":"model","options":[{"value":"model","name":"Model"}]}]""";

    /// <summary>
    /// Antigravity's recorded turn (<paramref name="fixture"/>) for prompt request <paramref name="recordedId"/>, after the prompt up to its reply, moved
    /// to <see cref="FakeLink.AcpSessionId"/>. Each line is the adapter's (<c>in</c>) or the client's (<c>out</c>).
    /// </summary>
    private static List<(string Dir, JsonObject Line)> RecordedTurn(long recordedId, string fixture = "agy-main.jsonl",
        string recordedSession = RecordedSession)
    {
        var lines = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "acp-v1", fixture))
            .Select(line => JsonNode.Parse(line.Replace(recordedSession, FakeLink.AcpSessionId))!.AsObject())
            .Where(line => line["dir"]!.GetValue<string>() is "in" or "out")
            .Select(line => (Dir: line["dir"]!.GetValue<string>(), Line: line["line"]!.AsObject())).ToList();
        var start = lines.FindIndex(line => line.Line["method"]?.GetValue<string>() == "session/prompt"
            && line.Line["id"]!.GetValue<long>() == recordedId);
        var end = lines.FindIndex(line => line.Dir == "in" && line.Line["method"] is null && line.Line["id"]?.GetValue<long>() == recordedId);
        return lines[(start + 1)..(end + 1)];
    }

    /// <summary>A host with one session whose first prompt went to a version 1 agent; returns the agent and the prompt.</summary>
    private static async Task<(FakeLink Agent, JsonElement Prompt)> OpenVersion1Turn(Host running, System.Net.WebSockets.ClientWebSocket socket,
        FakeLauncher launcher, string home)
    {
        await Command(socket, "agy", new StartSession("claude", "model", home, "Version 1", null, null, null));
        await running.WaitState("sessions", "agy", "idle");
        await Command(socket, "work", new SendMessage(null, "agy", "Work", "prompt"));
        var agent = await launcher.NextAsync();
        await agent.StartV1Async(V1Recording.Result("acp-v1/agy-main.jsonl", 1).GetRawText(), HostModel);
        return (agent, await agent.ReadAsync("session/prompt"));
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("remote")]
    public async Task Recorded_version_1_approval_is_answered_through_the_host_and_the_reply_ends_the_turn(string host)
    {
        var home = Directory.CreateTempSubdirectory("assembly-engine-v1-").FullName;
        var launcher = new FakeLauncher();
        try
        {
            await using var running = await Host.Start(host, home, null, launcher: launcher);
            using var socket = await running.Open();
            using var bulk = await running.Open(bulk: true);
            var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var needs = running.Runtime.Publisher.Register(item =>
            {
                if (item.Kind == "needs_me.opened") opened.TrySetResult(item.EntityId);
            }, out _);
            var (agent, prompt) = await OpenVersion1Turn(running, socket, launcher, home);
            var turn = RecordedTurn(5);
            string? needId = null;
            for (var i = 0; i < turn.Count; i++)
            {
                var (dir, line) = turn[i];
                if (dir == "out") continue; // the client's answer, read below
                if (line["method"] is null)
                {
                    await agent.ReplyAsync(prompt, line["result"]!.ToJsonString());
                    continue;
                }
                // The tool call and its approval came in the same millisecond (lines 14 and 15). One write keeps that, so
                // the send receipt, which the tool call releases, may land after the need opened.
                var asks = i + 1 < turn.Count && turn[i + 1].Line["method"]?.GetValue<string>() == "session/request_permission";
                await agent.SendAsync(asks ? line.ToJsonString() + "\n" + turn[++i].Line.ToJsonString() : line.ToJsonString());
                if (!asks) continue;

                needId = await opened.Task.WaitAsync(Timeout);
                await running.WaitState("messages", "work", "next_turn");
                await running.WaitState("needs_me", needId, "open");
                Assert.Equal("needs_me", (await Entity(bulk, "session", "agy")).GetProperty("state").GetString());
                var answer = new WindowCommand(Guid.NewGuid(), needId, 1, 0, new ResolveNeed("answered", JsonSerializer.SerializeToElement("allow")));
                var accepted = await Call(socket, "command", JsonSerializer.SerializeToElement(answer, EngineSocketJson.Default.WindowCommand));
                Assert.True(accepted.GetProperty("applied").GetBoolean(), accepted.ToString());
                var providerAnswer = await agent.NextMessageAsync().WaitAsync(Timeout);
                Assert.Equal(0, providerAnswer.GetProperty("id").GetInt32());
                Assert.Equal("allow", providerAnswer.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
                await running.WaitState("needs_me", needId, "answered");
                await WaitHostAnswerDone(running, needId);
                await running.WaitState("sessions", "agy", "running");
            }

            Assert.NotNull(needId);
            AssertHostAnswerExactlyOnce(running, needId);
            await running.WaitState("sessions", "agy", "idle");
        }
        finally { Directory.Delete(home, true); }
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("remote")]
    public async Task Recorded_version_1_stop_ends_the_turn_stopped_not_failed(string host)
    {
        var home = Directory.CreateTempSubdirectory("assembly-engine-v1-stop-").FullName;
        var launcher = new FakeLauncher();
        try
        {
            await using (var running = await Host.Start(host, home, null, launcher: launcher))
            {
                using var socket = await running.Open();
                using var bulk = await running.Open(bulk: true);
                var (agent, prompt) = await OpenVersion1Turn(running, socket, launcher, home);
                foreach (var (dir, line) in RecordedTurn(6))
                {
                    if (dir == "out")
                    {
                        // The recorded stop: the owner presses Stop, and the client sends session/cancel.
                        await running.WaitState("sessions", "agy", "running");
                        var session = await Entity(bulk, "session", "agy");
                        var stop = new WindowCommand(Guid.NewGuid(), "agy", session.GetProperty("version").GetInt64(), 0, new StopSession());
                        var stopped = await Call(socket, "command", JsonSerializer.SerializeToElement(stop, EngineSocketJson.Default.WindowCommand));
                        Assert.True(stopped.GetProperty("applied").GetBoolean(), stopped.ToString());
                        await agent.ReadAsync("session/cancel");
                    }
                    else if (line["method"] is null) await agent.ReplyAsync(prompt, line["result"]!.ToJsonString());
                    else await agent.SendAsync(line.ToJsonString());
                }
                await running.WaitState("sessions", "agy", "idle");
            }

            using var db = TestDatabase.Open(Path.Combine(home, "assembly.db"));
            var turns = db.Query("SELECT json_extract(payload,'$.state') FROM rows WHERE kind=$kind ORDER BY seq",
                row => row.Text(0), default, new SqlArg("$kind", RowKind.Turn));
            Assert.Equal("cancelled", turns[^1]);
            Assert.DoesNotContain("failed", turns);
        }
        finally { Directory.Delete(home, true); }
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("remote")]
    public async Task A_version_1_stop_with_an_approval_open_saves_the_tool_cancelled(string host)
    {
        var home = Directory.CreateTempSubdirectory("assembly-engine-v1-stop-ask-").FullName;
        var launcher = new FakeLauncher();
        try
        {
            await using (var running = await Host.Start(host, home, null, launcher: launcher))
            {
                using var socket = await running.Open();
                using var bulk = await running.Open(bulk: true);
                var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var needs = running.Runtime.Publisher.Register(item =>
                {
                    if (item.Kind == "needs_me.opened") opened.TrySetResult(item.EntityId);
                }, out _);
                var (agent, prompt) = await OpenVersion1Turn(running, socket, launcher, home);
                // Recorded: Stop while the card is open. Antigravity reports the waiting tool failed ("Rejected by user")
                // right after session/cancel, then says the request was cancelled and replies cancelled.
                var turn = RecordedTurn(3, "agy-stopask.jsonl", "55ada90b-107e-4a69-9950-d61196bf0630");
                var ask = turn.FindIndex(line => line.Line["method"]?.GetValue<string>() == "session/request_permission");
                await agent.SendAsync(turn[ask - 1].Line.ToJsonString() + "\n" + turn[ask].Line.ToJsonString());
                var needId = await opened.Task.WaitAsync(Timeout);
                await running.WaitState("needs_me", needId, "open");
                await running.WaitState("messages", "work", "next_turn");

                var session = await Entity(bulk, "session", "agy");
                var stop = new WindowCommand(Guid.NewGuid(), "agy", session.GetProperty("version").GetInt64(), 0, new StopSession());
                var stopped = await Call(socket, "command", JsonSerializer.SerializeToElement(stop, EngineSocketJson.Default.WindowCommand));
                Assert.True(stopped.GetProperty("applied").GetBoolean(), stopped.ToString());
                await agent.ReadAsync("session/cancel");
                foreach (var (dir, line) in turn.Skip(ask + 1).Where(line => line.Dir == "in"))
                {
                    if (line["method"] is null) await agent.ReplyAsync(prompt, line["result"]!.ToJsonString());
                    else await agent.SendAsync(line.ToJsonString());
                }
                await running.WaitState("sessions", "agy", "idle");
            }

            using var db = TestDatabase.Open(Path.Combine(home, "assembly.db"));
            var tools = db.Query("SELECT json_extract(payload,'$.state') FROM rows WHERE kind=$kind ORDER BY seq",
                row => row.Text(0), default, new SqlArg("$kind", RowKind.Tool));
            Assert.NotEmpty(tools);
            Assert.Equal("cancelled", tools[^1]);
            Assert.DoesNotContain("failed", tools);
        }
        finally { Directory.Delete(home, true); }
    }
}
