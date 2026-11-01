namespace Assembly.Tests.Features.Chat.Sessions;

// ACP v2 foreground snapshots and Task tool updates replace the AIR background/subagent extensions.
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

    [Fact]
    public async Task V2_Task_tool_shows_subagent_progress_beneath_its_parent()
    {
        _host.Send("review");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"child","name":"Task","title":"Review code","status":"in_progress"}""");
        var started = (await RowsUntil(row => row.Kind == "subagent"))[^1];
        Assert.Equal("running", started.Payload.GetProperty("state").GetString());
        Assert.Equal(_session.SessionId, started.Payload.GetProperty("sessionId").GetString());
        Assert.Equal("child", started.Payload.GetProperty("toolCallId").GetString());
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"child","status":"completed"}""");
        var ended = (await RowsUntil(row => row.Kind == "subagent"))[^1];
        Assert.Equal("idle", ended.Payload.GetProperty("state").GetString());
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }
}
