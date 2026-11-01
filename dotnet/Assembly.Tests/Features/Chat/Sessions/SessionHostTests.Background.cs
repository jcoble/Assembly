namespace Assembly.Tests.Features.Chat.Sessions;

// ACP v2 foreground snapshots and native child updates replace the AIR extensions.
public sealed partial class SessionHostTests
{
    private static readonly string Background =
        """{"sessionUpdate":"state_update","state":"idle","background":[{"id":"build","title":"Build","state":"running"}]}""";
    private static readonly string NoBackground =
        """{"sessionUpdate":"state_update","state":"idle","background":[]}""";

    private async Task<(FakeLink Agent, string Run)> StartBackgroundAsync()
    {
        var run = _host.Send("start build")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync(Background);
        await RowsUntil(row => row.Kind == "task");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn");
        await Until(() => _host.ActiveRunId is null);
        return (agent, run);
    }

    [Fact]
    public async Task V2_background_keeps_the_session_open_until_the_background_list_is_empty()
    {
        var (agent, _) = await StartBackgroundAsync();
        Assert.True(_host.IsOpen);
        await agent.UpdateAsync(NoBackground);
        var task = (await RowsUntil(row => row.Kind == "task"))[^1];
        Assert.Equal("completed", task.Payload.GetProperty("state").GetString());
        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
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
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn","background":[]}""");
        var end = await RowsUntil(row => row.Kind == "turn");
        Assert.Equal(run, end.Last(row => row.Kind == "turn").RunId);
        await agent.CloseAsync();
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
