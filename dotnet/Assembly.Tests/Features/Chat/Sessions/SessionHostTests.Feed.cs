using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Topics;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task Feed_keeps_immediate_pending_but_records_only_real_provider_state_answer_and_final_usage()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        var feed = new EngineFeed(runtime, _log);
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: feed);
        var host = registry.For(_session.SessionId);
        var run = host.Send("hi", "owner-client")!;
        Assert.Equal(["user", "turn"], await Kinds(2));
        Assert.Equal("idle", EngineState()); // pending is immediate while initialize is deliberately delayed
        var agent = await _launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal("idle", EngineState());
        Assert.Equal(1, _database.QueryFirst("SELECT can_steer FROM engine_session", r => r.Long(0), default));
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"running"}""");
        await agent.TextAsync("ready"); await RowsUntil(row => row.Kind == "delta");
        Assert.Equal("running", EngineState());
        await agent.RequestAsync(7, "session/request_permission", Permission.ReplaceLineEndings(""));
        var ask = (await RowsUntil(row => row.Kind == "ask"))[^1];
        var askId = ask.ItemId!["ask:".Length..];
        Assert.Equal("needs_me", EngineState());
        Assert.True(host.Answer(askId, JsonSerializer.SerializeToElement(new { optionId = "yes" })));
        await agent.ResultAsync(); await RowsUntil(row => row.Kind == "ask" && row.Payload.GetProperty("state").GetString() == "answered");
        Assert.Equal("answered", _database.QueryFirst("SELECT state FROM engine_needs_me WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", askId)));
        Assert.EndsWith(":7", _database.QueryFirst("SELECT provider_request_id FROM engine_needs_me WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", askId)));
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn","usage":{"inputTokens":8,"outputTokens":3,"cachedReadTokens":80081,"cachedWriteTokens":12002,"costUsd":0.25}}""");
        await agent.CloseAsync();
        Assert.Equal("idle", EngineState());
        var usage = _database.QueryFirst("SELECT provider_turn_id,input_tokens,cached_tokens,cost_nanodollars FROM engine_usage", r => (r.Text(0), r.Long(1), r.Long(2), r.Long(3)), default);
        Assert.Equal(("prompt-" + prompt.GetProperty("id").GetRawText(), 92091L, 80081L, 250000000L), usage);
        Assert.NotEqual(run, usage.Item1);
        Assert.Equal(0, _database.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'", r => r.Long(0), default));
        string? EngineState() => _database.QueryFirst("SELECT state FROM engine_session WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", _session.SessionId));
    }

    [Fact]
    public async Task Real_turn_cancel_settles_chat_and_Engine_need_in_the_same_write()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: new EngineFeed(runtime, _log));
        var host = registry.For(_session.SessionId); var run = host.Send("hi")!;
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.RequestAsync(7, "session/request_permission", Permission.ReplaceLineEndings(""));
        var ask = (await RowsUntil(row => row.Kind == "ask"))[^1];
        Assert.True(host.Stop(run));
        await agent.ReadAsync("session/cancel");
        // The cancellation settles the permission before the real provider idle acknowledgement.
        await agent.ResultAsync();
        var settled = (await RowsUntil(row => row.Kind == "ask" && row.Payload.GetProperty("state").GetString() == "cancelled"))[^1];
        Assert.Equal(ask.ItemId, settled.ItemId);
        Assert.Equal("cancelled", _database.QueryFirst("SELECT state FROM engine_needs_me", r => r.Text(0), default));
        await agent.FinishAsync(prompt, """{"stopReason":"cancelled"}"""); await agent.CloseAsync();
        Assert.Equal(0, _database.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'", r => r.Long(0), default));
    }

    [Fact]
    public async Task Immediate_native_child_ask_keeps_real_child_title_and_one_parent_Engine_need()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: new EngineFeed(runtime, _log));
        var host = registry.For(_session.SessionId); host.Send("hi");
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        var member = NativeUpdates("claude").First(e => e.GetProperty("sessionUpdate").GetString() == "subagent_update");
        var childId = member.GetProperty("sessionId").GetString()!;
        await agent.UpdateAsync(member.GetRawText());
        // Do not wait for the asynchronous host pump before the next real RPC request.
        await agent.RequestAsync(7, "session/request_permission", JsonSerializer.Serialize(new { sessionId = childId, title = "Read?", options = new[] { new { optionId = "yes", name = "Yes", kind = "allow_once" } } }));
        var ask = (await RowsUntil(row => row.Kind == "ask"))[^1];
        Assert.Equal(member.GetProperty("title").GetString() + ": Read?", ask.Payload.GetProperty("title").GetString());
        Assert.True(host.Answer(ask.ItemId!["ask:".Length..], JsonSerializer.SerializeToElement(new { optionId = "yes" })));
        await agent.ResultAsync(); await RowsUntil(row => row.Kind == "ask" && row.Payload.GetProperty("state").GetString() == "answered");
        Assert.Equal(_session.SessionId, _database.QueryFirst("SELECT session_id FROM engine_needs_me", r => r.Text(0), default));
        Assert.Equal(1, _database.QueryFirst("SELECT COUNT(*) FROM engine_session", r => r.Long(0), default));
        Assert.Null(_sessionStore.Get(childId));
        await agent.UpdateAsync(JsonSerializer.Serialize(new { sessionUpdate = "subagent_update", sessionId = childId, state = new { state = "idle", stopReason = "end_turn" } }));
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
    }

    [Fact]
    public async Task Stopping_one_native_child_settles_only_its_ask_and_keeps_parent_and_sibling_alive()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: new EngineFeed(runtime, _log));
        var host = registry.For(_session.SessionId); var run = host.Send("hi");
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        foreach (var child in new[] { "one", "two" })
            await agent.UpdateAsync(JsonSerializer.Serialize(new
            {
                sessionUpdate = "subagent_update",
                sessionId = child,
                title = child,
                state = new { state = "running" },
                capabilities = new { cancel = new { } }
            }));
        foreach (var (id, session) in new[] { (7, "one"), (8, "two"), (9, FakeLink.AcpSessionId) })
        {
            await agent.RequestAsync(id, "session/request_permission", JsonSerializer.Serialize(new
            {
                sessionId = session,
                title = session,
                options = new[] { new { optionId = "yes", name = "Yes", kind = "allow_once" } }
            }));
            await RowsUntil(row => row.Kind == "ask" && row.Payload.GetProperty("title").GetString()!.Contains(session));
        }
        var stopping = host.StopSubagentAsync("one", default);
        var messages = new[] { await agent.NextMessageAsync(), await agent.NextMessageAsync() };
        Assert.Equal("one", messages.Single(m => m.TryGetProperty("method", out _)).GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(7, messages.Single(m => m.TryGetProperty("result", out _)).GetProperty("id").GetInt32());
        await RowsUntil(row => row.Kind == "ask" && row.Payload.GetProperty("state").GetString() == "cancelled");
        Assert.Equal(2, _database.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'", r => r.Long(0), default));
        Assert.Equal(run, host.ActiveRunId);
        Assert.False(stopping.IsCompleted); // only real child idle acknowledges Stop
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}""", "one");
        Assert.True(await stopping.WaitAsync(TimeSpan.FromSeconds(5)));
        foreach (var ask in _store.PageBefore(_session.SessionId, long.MaxValue, 100).Rows.Where(r => r.Kind == "ask" && r.Payload.GetProperty("state").GetString() == "open"))
            Assert.True(host.Answer(ask.ItemId!["ask:".Length..], JsonSerializer.SerializeToElement(new { optionId = "yes" })));
        await agent.ResultAsync(); await agent.ResultAsync();
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"one","state":{"state":"idle","stopReason":"cancelled"}}""");
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"two","state":{"state":"idle","stopReason":"end_turn"}}""");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
    }

    [Theory]
    [InlineData("claude", "claude-terminal-snapshots")]
    [InlineData("codex", "codex-terminal-background")]
    public async Task Actual_v2_terminal_and_native_fixtures_keep_one_owning_session_and_record_tools(string provider, string fixture)
    {
        var session = _sessionStore.Create(provider, "https://github.com/o/r");
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: new EngineFeed(runtime, _log));
        var host = registry.For(session.SessionId);
        using var live = _hub.Subscribe(session.SessionId, 0);
        host.Send("fixture");
        var agent = await _launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "acp-v2", fixture + ".jsonl")))
        {
            using var doc = JsonDocument.Parse(line);
            await agent.UpdateAsync(doc.RootElement.GetProperty("line").GetProperty("params").GetProperty("update").GetRawText());
        }
        foreach (var update in NativeUpdates(provider)) await agent.UpdateAsync(update.GetRawText());
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        Assert.Equal(2, _database.QueryFirst("SELECT COUNT(*) FROM engine_session", r => r.Long(0), default));
        Assert.True(_database.QueryFirst("SELECT COUNT(*) FROM engine_tool_call WHERE session_id=$id AND boundary='acp'", r => r.Long(0), default, new SqlArg("$id", session.SessionId)) > 0);
        Assert.Equal(1, _database.QueryFirst("SELECT COUNT(*) FROM engine_usage WHERE session_id=$id", r => r.Long(0), default, new SqlArg("$id", session.SessionId)));
        var rows = _store.PageBefore(session.SessionId, long.MaxValue, 500).Rows;
        Assert.Single(rows, r => r.Kind == "user");
        Assert.Contains(rows, r => r.Kind == "subagent");
        Assert.Contains(rows, r => r.Kind == "tool");
    }
}
