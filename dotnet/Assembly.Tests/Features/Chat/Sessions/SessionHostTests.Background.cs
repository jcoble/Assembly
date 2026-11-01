namespace Assembly.Tests.Features.Chat.Sessions;

// ACP v2 foreground snapshots and native child updates replace the AIR extensions.
public sealed partial class SessionHostTests
{
    private static readonly string Background =
        """{"sessionUpdate":"tool_call_update","toolCallId":"build","title":"Build","status":"in_progress","content":[{"type":"terminal","terminalId":"terminal"}]}""";
    private static readonly string NoBackground =
        """{"sessionUpdate":"tool_call_update","toolCallId":"build","status":"completed"}""";

    private async Task<(FakeLink Agent, string Run)> StartBackgroundAsync()
    {
        var run = _host.Send("start build")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync(Background);
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "task");
        await RowsUntil(row => row.Kind == "turn");
        await Until(() => _host.ActiveRunId is null);
        return (agent, run);
    }

    [Fact]
    public async Task Known_tool_keeps_session_past_grace_until_its_explicit_status_ends()
    {
        var (agent, _) = await StartBackgroundAsync();
        var next = agent.NextMessageAsync();
        _time.Advance(TimeSpan.FromMilliseconds(5016));
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"build","title":"Still running"}""");
        await RowsUntil(row => row.Kind == "tool");
        Assert.True(_host.IsOpen);
        Assert.False(next.IsCompleted);
        await agent.UpdateAsync("""{"sessionUpdate":"terminal_output_chunk","terminalId":"terminal","data":"ZmluaXNoZWQK"}""");
        await agent.UpdateAsync("""{"sessionUpdate":"terminal_update","terminalId":"terminal","exitStatus":{"exitCode":0}}""");
        await agent.UpdateAsync(NoBackground);
        var task = (await RowsUntil(row => row.Kind == "task"))[^1];
        Assert.Equal("completed", task.Payload.GetProperty("state").GetString());
        var close = await next.WaitAsync(Wait);
        Assert.Equal("session/close", close.GetProperty("method").GetString());
        Assert.Equal(FakeLink.AcpSessionId, close.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal("finished\n", _store.ToolCall(_session.SessionId, "build")!.Output);
        await agent.ReplyAsync(close, "{}");
    }

    [Fact]
    public async Task Adapter_loss_ends_v2_background_work_without_claiming_completion()
    {
        var (agent, run) = await StartBackgroundAsync();
        agent.Crash();
        var task = (await RowsUntil(row => row.Kind == "task"))[^1];
        Assert.Equal(run, task.RunId);
        Assert.Equal("stopped", task.Payload.GetProperty("state").GetString());
    }

    [Fact]
    public async Task V2_foreground_resuming_after_background_work_has_its_own_run()
    {
        var (agent, previous) = await StartBackgroundAsync();
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"running"}""");
        await agent.TextAsync("Finished.", "follow-up");
        var rows = await RowsUntil(row => row.Kind == "delta");
        var run = rows.Last(row => row.Kind == "delta").RunId;
        Assert.NotEqual(previous, run);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        var end = await RowsUntil(row => row.Kind == "turn");
        Assert.Equal(run, end.Last(row => row.Kind == "turn").RunId);
        await agent.UpdateAsync(NoBackground);
        await agent.CloseAsync();
    }

    [Fact]
    public async Task Synthetic_prompt_error_retains_known_work_once_until_cancelled_status()
    {
        var run = _host.Send("start")!;
        var agent = await _launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync(Background);
        await RowsUntil(row => row.Kind == "tool");
        await agent.ReplyErrorAsync(prompt, -32603, "synthetic error");
        var end = await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "failed");
        Assert.Single(end, row => row.Kind == "task");
        Assert.True(_host.IsOpen);
        Assert.Equal("started", _store.PageBefore(_session.SessionId, long.MaxValue, 100).Rows.Single(row => row.Kind == "tool").Payload.GetProperty("state").GetString());
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"build","status":"cancelled"}""");
        var task = (await RowsUntil(row => row.Kind == "task"))[^1];
        Assert.Equal((run, "stopped"), (task.RunId, task.Payload.GetProperty("state").GetString()));
        await agent.CloseAsync();
    }

    [Fact]
    public async Task Retained_tool_keeps_original_run_during_new_turn_and_two_tools_settle_independently()
    {
        var (agent, original) = await StartBackgroundAsync();
        var nextRun = _host.Send("another turn")!;
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"second","status":"in_progress"}""");
        await agent.FinishAsync(prompt, """{"stopReason":"cancelled"}""");
        await RowsUntil(row => row.Kind == "task" && row.ItemId == "task:second");
        await RowsUntil(row => row.Kind == "turn");
        await agent.UpdateAsync("""{"sessionUpdate":"terminal_update","terminalId":"terminal","exitStatus":{}}""");
        foreach (var status in new[] { "null", "\"pending\"", "\"future\"" })
            await agent.UpdateAsync("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"build\",\"status\":" + status + "}");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"unknown","background":[]}""");
        await agent.UpdateAsync(NoBackground);
        var first = (await RowsUntil(row => row.Kind == "task" && row.ItemId == "task:build"))[^1];
        Assert.Equal(original, first.RunId); Assert.True(_host.IsOpen);
        var tool = _store.PageBefore(_session.SessionId, long.MaxValue, 100).Rows.Single(row => row.Kind == "tool" && row.ItemId == "build");
        Assert.Equal(original, tool.RunId);
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"second","status":"failed"}""");
        var second = (await RowsUntil(row => row.Kind == "task" && row.ItemId == "task:second"))[^1];
        Assert.Equal((nextRun, "failed"), (second.RunId, second.Payload.GetProperty("state").GetString()));
        await agent.CloseAsync();
    }

    [Fact]
    public async Task Full_shutdown_closes_retained_session_and_stops_work_without_inventing_terminal_exit()
    {
        var (agent, _) = await StartBackgroundAsync();
        await agent.UpdateAsync("""{"sessionUpdate":"terminal_output_chunk","terminalId":"terminal","data":"eA=="}""");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"build","title":"Output received"}""");
        await RowsUntil(row => row.Kind == "tool");
        var shutdown = _host.StopForShutdownAsync();
        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
        await shutdown.WaitAsync(Wait);
        Assert.False(_host.IsOpen);
        var stopped = (await RowsUntil(row => row.Kind == "task"))[^1];
        Assert.Equal("stopped", stopped.Payload.GetProperty("state").GetString());
        var detail = _store.ToolCall(_session.SessionId, "build")!;
        Assert.Equal("x", detail.Output);
        Assert.Null(Assert.Single(detail.Terminals!).ExitStatus);
    }

    [Fact]
    public async Task Genuine_codex_idle_then_late_terminal_sequence_matches_replayed_detail_and_task_identity()
    {
        _host.Send("captured command");
        var agent = await _launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"user_message","messageId":"ack","content":[]}""");
        await agent.ReplyAsync(prompt, """{"messageId":"ack"}""");
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "acp-v2", "codex-terminal-background.jsonl");
        string? toolId = null;
        foreach (var line in File.ReadLines(path))
        {
            using var frame = System.Text.Json.JsonDocument.Parse(line);
            var update = frame.RootElement.GetProperty("line").GetProperty("params").GetProperty("update");
            await agent.UpdateAsync(update.GetRawText());
            if (update.GetProperty("sessionUpdate").GetString() == "tool_call_update") toolId = update.GetProperty("toolCallId").GetString();
        }
        await agent.CloseAsync();
        var detail = _store.ToolCall(_session.SessionId, toolId!)!;
        using var replayFile = new TempDatabase(); using var replayDb = TestDatabase.Open(replayFile.Path);
        var replayStore = Assembly.Application.Features.Chat.Rows.RowStore.Open(replayDb);
        var converter = new Assembly.Application.Features.Chat.Rows.RowConverter();
        foreach (var e in Assembly.Tests.Fixtures.FixtureReplay.Events("acp-v2/codex-terminal-background.jsonl"))
            replayStore.Commit("replay", converter.Apply(e));
        var replay = replayStore.ToolCall("replay", toolId!)!;
        Assert.Equal(("finished\n", 9L, false), (detail.Output, detail.OutputBytes, detail.OutputTruncated));
        Assert.Equal((replay.Output, replay.OutputBytes, replay.OutputTruncated), (detail.Output, detail.OutputBytes, detail.OutputTruncated));
        Assert.Single(_store.PageBefore(_session.SessionId, long.MaxValue, 100).Rows, r => r.ItemId == "task:" + toolId && r.Payload.GetProperty("state").GetString() == "completed");
        Assert.Single(_store.PageBefore(_session.SessionId, long.MaxValue, 100).Rows, r => r.Kind == "tool");
    }

    [Theory]
    [InlineData("claude", "idle", "end_turn", true)]
    [InlineData("codex", "unknown", null, false)]
    public async Task Genuine_native_children_persist_one_row_under_parent_without_duplicate_tools_or_prompts(
        string provider, string finalState, string? stopReason, bool canCancel)
    {
        _host.Send("review");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        string? childId = null;
        var childCount = 0;
        foreach (var update in NativeUpdates(provider))
        {
            await agent.UpdateAsync(update.GetRawText());
            if (update.GetProperty("sessionUpdate").GetString() != "subagent_update") continue;
            childId ??= update.GetProperty("sessionId").GetString();
            var row = (await RowsUntil(row => row.Kind == "subagent"))[^1];
            Assert.Equal("subagent:" + childId, row.ItemId);
            Assert.Equal(_session.SessionId, row.Payload.GetProperty("sessionId").GetString());
            Assert.False(row.Payload.GetProperty("canStop").GetBoolean());
            Assert.True(!row.Payload.TryGetProperty("toolCallId", out var tool) || tool.ValueKind == System.Text.Json.JsonValueKind.Null);
            childCount++;
        }
        Assert.Equal(provider == "claude" ? 2 : 7, childCount);
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync(); // Unknown child activity must not keep the parent's adapter alive.
        using var reopened = TestDatabase.Open(_db.Path);
        var saved = Assembly.Application.Features.Chat.Rows.RowStore.Open(reopened).PageBefore(_session.SessionId, long.MaxValue, 500).Rows;
        var child = Assert.Single(saved, row => row.Kind == "subagent");
        Assert.Equal("subagent:" + childId, child.ItemId);
        Assert.Equal(finalState, child.Payload.GetProperty("state").GetString());
        Assert.Equal(stopReason, child.Payload.TryGetProperty("stopReason", out var reason) ? reason.GetString() : null);
        Assert.Equal(canCancel, child.Payload.GetProperty("canCancel").GetBoolean());
        Assert.Single(saved, row => row.Kind == "user");
        Assert.Contains(saved, row => row.Kind == "tool");
        Assert.Null(_sessionStore.Get(childId!)); // No Assembly operational child was created.
    }

    [Fact]
    public async Task Native_child_clears_are_saved_after_running_and_do_not_imply_cancellation()
    {
        _host.Send("review");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"native","title":"Review","description":"Role","state":{"state":"running"},"capabilities":{"cancel":{}}}""");
        await RowsUntil(row => row.Kind == "subagent");
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"native","title":null,"description":null,"state":null,"capabilities":null}""");
        var cleared = (await RowsUntil(row => row.Kind == "subagent"))[^1];
        Assert.Equal("", cleared.Payload.GetProperty("title").GetString());
        Assert.True(!cleared.Payload.TryGetProperty("description", out var description) || description.ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.Equal("unknown", cleared.Payload.GetProperty("state").GetString());
        Assert.False(cleared.Payload.GetProperty("canCancel").GetBoolean());
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    // Original selected lines from A11's verified Claude 0.99.1 / Codex 2.5.0 captures, SDK 2.1.0.
    // Keep child-transcript views separate; the live host owns only the parent's subscription.
    private static IEnumerable<System.Text.Json.JsonElement> NativeUpdates(string provider)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "acp-v2", provider + "-native-subagents.jsonl");
        string? parentId = null;
        foreach (var line in File.ReadLines(path))
        {
            using var entry = System.Text.Json.JsonDocument.Parse(line);
            var parameters = entry.RootElement.GetProperty("line").GetProperty("params");
            parentId ??= parameters.GetProperty("sessionId").GetString();
            if (parameters.GetProperty("sessionId").GetString() == parentId)
                yield return parameters.GetProperty("update").Clone();
        }
    }
}
