using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat;
using Assembly.Domain.Sessions;

namespace Assembly.Tests.Features.Chat.Sessions;

// SessionHostTests, continued: background work, runs the agent starts itself, and subagents.
public sealed partial class SessionHostTests
{
    [Fact]
    public async Task Background_work_keeps_the_session_open_and_what_the_agent_does_after_it_is_a_run_of_its_own()
    {
        var (agent, runId) = await StartClaudeBackgroundAsync();
        Assert.Null(_host.ActiveRunId);
        Assert.Contains("session kept open for background work", _log.ToString());

        // With no prompt running, the agent goes on by itself: a run of its own, with no message of the user's.
        await agent.TextAsync("It printed finished.", "m2");
        var started = await RowsUntil(row => row.Kind == "delta");
        Assert.Equal(["turn", "delta"], started.Select(row => row.Kind));
        var selfRun = started[0].RunId!;
        Assert.NotEqual(runId, selfRun);
        Assert.Equal("started", started[0].Payload.GetProperty("state").GetString());
        Assert.Equal("Continued after a background task finished", started[0].Payload.GetProperty("reason").GetString());
        Assert.Equal(selfRun, _host.ActiveRunId);

        // Claude's tag on the cycle's usage ends the run, and the untracked task with it; then the session closes.
        await agent.UpdateAsync(TaskNotification);
        var end = await RowsUntil(row => row.Kind == "turn");
        Assert.Equal(["context", "task", "message", "stats", "turn"], end.Select(row => row.Kind));
        Assert.Equal("completed", end[1].Payload.GetProperty("state").GetString());
        Assert.Equal(runId, end[1].RunId);
        Assert.Equal("finished", end[4].Payload.GetProperty("state").GetString());
        Assert.Equal(selfRun, end[4].RunId);
        Assert.Null(_host.ActiveRunId);
        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
    }

    [Fact]
    public async Task An_adapter_that_dies_under_a_kept_session_ends_its_background_work()
    {
        var (agent, runId) = await StartClaudeBackgroundAsync();

        agent.Crash();
        var task = (await RowsUntil(row => row.Kind == "task"))[^1];
        Assert.Equal((runId, "stopped"), (task.RunId, task.Payload.GetProperty("state").GetString()));
        // The session takes sends again, on a new adapter.
        Assert.NotNull(_host.Send("again"));
        await (await _launcher.NextAsync()).StartAsync();
    }

    [Fact]
    public async Task A_message_that_reaches_a_run_the_agent_started_as_it_ends_starts_the_next_turn()
    {
        var (agent, _) = await StartClaudeBackgroundAsync();
        await agent.TextAsync("It finished.", "m2");
        await RowsUntil(row => row.Kind == "delta");

        var steering = _host.SteerAsync("next", null, CancellationToken.None);
        var steer = await agent.ReadAsync("_session/steering");
        await agent.ReplyAsync(steer, """{"outcome":"promptRequired","reason":"noRunningTurn"}""");
        await agent.UpdateAsync(TaskNotification);
        var next = await steering.WaitAsync(Wait);
        Assert.NotNull(next);
        // The session closed when the run and its task ended, so the next turn resumes it.
        await agent.CloseAsync();
        var prompt = await agent.ReadAsync("session/resume");
        await agent.ReplyAsync(prompt, "{}");
        Assert.Equal("next", (await agent.ReadAsync("session/prompt")).GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_send_while_only_background_work_runs_prompts_the_open_session()
    {
        var (agent, _) = await StartClaudeBackgroundAsync();

        var next = _host.Send("anything new?")!;
        Assert.Equal(["user", "turn"], await Kinds(2));
        // Straight to the prompt: the session is still open, so nothing is opened or resumed.
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        var end = await RowsUntil(row => row.Kind == "turn");
        Assert.Equal(next, end[^1].RunId);

        await agent.UpdateAsync(TaskNotification);
        Assert.Equal("completed", (await RowsUntil(row => row.Kind == "task"))[^1].Payload.GetProperty("state").GetString());
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_backgrounded_command_finishing_after_its_turn_stays_in_that_turn_and_starts_no_run()
    {
        var runId = _host.Send("start it")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"sleep 15","kind":"execute","status":"in_progress"}""");
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_spawned","asyncTaskId":"t1","name":"sleep 15","canStop":true,"toolCallId":"t1"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        // Codex's end of the task names no task, then the tool call it ran in finishes.
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_state_update","asyncTaskId":"t1","state":"completed"}""");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"t1","status":"completed"}""");
        var late = await RowsUntil(row => row.Kind == "tool");
        Assert.Equal(["task", "tool"], late.Select(row => row.Kind));
        Assert.All(late, row => Assert.Equal(runId, row.RunId));
        Assert.Equal("""{"name":"sleep 15","state":"completed","canStop":false,"toolCallId":"t1"}""", late[0].Payload.GetRawText());
        Assert.Null(_host.ActiveRunId);
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_task_the_agent_tells_of_just_after_its_answer_still_keeps_the_session()
    {
        var runId = _host.Send("start it")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"sleep 15","kind":"execute","status":"in_progress"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");
        await Until(() => _log.ToString().Contains("kept open"));

        await agent.UpdateAsync("""{"sessionUpdate":"async_task_spawned","asyncTaskId":"t1","name":"sleep 15","canStop":true,"toolCallId":"t1"}""");
        var task = (await RowsUntil(row => row.Kind == "task"))[^1];
        Assert.Equal((runId, "running"), (task.RunId, task.Payload.GetProperty("state").GetString()));
        _time.Advance(TimeSpan.FromMilliseconds(SessionHost.TaskGraceMs));
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_state_update","asyncTaskId":"t1","state":"completed"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_turn_that_ends_with_a_tool_running_and_no_task_closes_its_session_after_a_moment()
    {
        _host.Send("start it");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"ls","kind":"execute","status":"in_progress"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");
        // Kept for the grace, so the clock moves only once the grace's deadline is set.
        await Until(() => _log.ToString().Contains("kept open"));

        _time.Advance(TimeSpan.FromMilliseconds(SessionHost.TaskGraceMs));
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_task_the_agent_can_stop_is_stopped_on_request()
    {
        _host.Send("start it");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_spawned","asyncTaskId":"x1","name":"sleep 15","canStop":true,"toolCallId":"t1"}""");
        var task = (await RowsUntil(row => row.Kind == "task"))[^1];
        Assert.Equal("task:x1", task.ItemId);
        Assert.Equal("""{"name":"sleep 15","state":"running","canStop":true,"toolCallId":"t1"}""", task.Payload.GetRawText());

        Assert.False(await _host.StopTaskAsync("some-other-task", CancellationToken.None));
        var stopping = _host.StopTaskAsync("x1", CancellationToken.None);
        var stop = await agent.ReadAsync("_session/async_task/stop");
        Assert.Equal("x1", stop.GetProperty("params").GetProperty("asyncTaskId").GetString());
        await agent.ReplyAsync(stop, """{"stopped":true}""");
        Assert.True(await stopping.WaitAsync(Wait));

        await agent.UpdateAsync("""{"sessionUpdate":"async_task_state_update","asyncTaskId":"x1","state":"cancelled"}""");
        Assert.Equal("stopped", (await RowsUntil(row => row.Kind == "task"))[^1].Payload.GetProperty("state").GetString());
        Assert.False(await _host.StopTaskAsync("x1", CancellationToken.None));
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_run_the_agent_started_that_goes_quiet_ends_after_a_while()
    {
        _host.Send("start it");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_spawned","asyncTaskId":"x1","name":"sleep 15","canStop":false}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        await agent.TextAsync("Waiting on it.", "m2");
        var selfRun = (await RowsUntil(row => row.Kind == "delta"))[0].RunId;
        // The task's end doesn't end the run; the agent never says its cycle is over.
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_state_update","asyncTaskId":"x1","state":"completed"}""");
        await RowsUntil(row => row.Kind == "task");

        _time.Advance(TimeSpan.FromMilliseconds(SessionHost.QuietMs));
        var end = await RowsUntil(row => row.Kind == "turn");
        Assert.Equal(selfRun, end[^1].RunId);
        Assert.Equal("finished", end[^1].Payload.GetProperty("state").GetString());
        Assert.Contains("no word from the agent for 30 s", _log.ToString());
        await agent.CloseAsync();
    }

    [Fact]
    public async Task Shutting_down_stops_background_work_before_closing_its_session()
    {
        _host.Send("start it");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_spawned","asyncTaskId":"x1","name":"sleep 15","canStop":true}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        var shutdown = _host.CloseKeptForShutdownAsync();
        var stop = await agent.ReadAsync("_session/async_task/stop");
        await agent.ReplyAsync(stop, """{"stopped":true}""");
        await agent.CloseAsync();
        await shutdown.WaitAsync(Wait);
        Assert.Equal("stopped", (await RowsUntil(row => row.Kind == "task"))[^1].Payload.GetProperty("state").GetString());
    }

    private const string TaskNotification =
        """{"sessionUpdate":"usage_update","used":1,"size":10,"_meta":{"_claude/origin":{"kind":"task-notification"}}}""";

    // A turn in which Claude backgrounds a Bash command, through to its end; the task row is still running.
    private const string ChildAnnounced =
        """{"sessionUpdate":"subagent_update","sessionId":"child1","title":"general-purpose","description":"Count lines","capabilities":{"cancel":{}},"state":{"state":"running"}}""";

    private const string ChildPrompt =
        """{"sessionUpdate":"session_message","messageId":"task1","content":[{"type":"text","text":"Count the lines of notes.txt"}]}""";

    [Fact]
    public async Task A_subagent_is_a_session_of_its_own_nested_under_the_tool_call_that_started_it()
    {
        var runId = _host.Send("count the lines")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"task1","title":"Task","kind":"think","status":"pending"}""");
        await agent.UpdateAsync(ChildAnnounced);
        await agent.UpdateAsync(ChildPrompt, "child1");
        await agent.TextAsync("3 lines", "c1", "child1");
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"child1","state":{"state":"idle","stopReason":"end_turn"}}""");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"task1","status":"completed"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        var rows = await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        // Announced, nested once its prompt names the Task call, then idle; all in the run that started it.
        var subagent = rows.Where(row => row.Kind == "subagent").ToList();
        Assert.Equal(["running", "running", "idle"], subagent.Select(row => row.Payload.GetProperty("state").GetString()));
        Assert.All(subagent, row => Assert.Equal((runId, "subagent:child1"), (row.RunId, row.ItemId)));
        Assert.Equal(JsonValueKind.Null, subagent[0].Payload.TryGetProperty("toolCallId", out var none) ? none.ValueKind : JsonValueKind.Null);
        Assert.Equal("task1", subagent[^1].Payload.GetProperty("toolCallId").GetString());
        Assert.False(subagent[^1].Payload.GetProperty("canStop").GetBoolean());
        Assert.True(subagent[0].Payload.GetProperty("canStop").GetBoolean());

        // Its session is the parent's child, kept out of the list, with its prompt, its reply, and its end.
        var child = _sessionStore.Get(subagent[0].Payload.GetProperty("sessionId").GetString()!)!;
        Assert.Equal((_session.SessionId, "child1", "general-purpose"), (child.ParentSessionId, child.AcpSessionId, child.Title));
        Assert.DoesNotContain(_sessionStore.List(null, 50).Sessions, session => session.SessionId == child.SessionId);
        var childRows = _store.RowsAfter(child.SessionId, 0);
        Assert.Equal("Count the lines of notes.txt", childRows.First(row => row.Kind == "user").Payload.GetProperty("text").GetString());
        Assert.Contains(childRows, row => row.Kind == "message");
        Assert.Equal("finished", childRows.Last(row => row.Kind == "turn").Payload.GetProperty("state").GetString());
        Assert.DoesNotContain(_store.RowsAfter(_session.SessionId, 0), row => row.Kind is "message" or "delta" && row.ItemId == "c1");
    }

    [Fact]
    public async Task A_subagent_s_handback_report_is_its_reply_not_a_tool_call()
    {
        _host.Send("count the lines");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync(ChildAnnounced);
        await agent.UpdateAsync(ChildPrompt, "child1");
        // As recorded: the report is the handback call's input; its output only says it was delivered.
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"hb1","title":"SubagentHandback","kind":"other","status":"pending","rawInput":{"message":"notes.txt has 3 lines."},"_meta":{"claudeCode":{"toolName":"SubagentHandback"}}}""", "child1");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"hb1","status":"completed","rawOutput":[{"type":"text","text":"{\"success\":true,\"message\":\"Report delivered to your caller.\"}"}]}""", "child1");
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"child1","state":{"state":"idle","stopReason":"end_turn"}}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        var rows = await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        var child = rows.First(row => row.Kind == "subagent").Payload.GetProperty("sessionId").GetString()!;
        var childRows = _store.RowsAfter(child, 0);
        Assert.DoesNotContain(childRows, row => row.Kind == "tool");
        Assert.Contains("notes.txt has 3 lines.", childRows.Last(row => row.Kind == "message").Payload.GetRawText());
    }

    [Fact]
    public async Task A_subagent_still_at_work_keeps_the_session_open_and_stops_on_its_own()
    {
        _host.Send("count the lines in the background");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync(ChildAnnounced);
        await agent.UpdateAsync(ChildPrompt, "child1");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await Until(() => _log.ToString().Contains("kept open"));

        var stopping = _host.StopTaskAsync("child1", CancellationToken.None);
        var cancel = await agent.ReadAsync("session/cancel");
        Assert.Equal("child1", cancel.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.True(await stopping);

        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"child1","state":{"state":"idle","stopReason":"cancelled"}}""");
        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
        var child = _sessionStore.Get(_store.RowsAfter(_session.SessionId, 0).Last(row => row.Kind == "subagent").Payload.GetProperty("sessionId").GetString()!)!;
        Assert.Equal("cancelled", _store.RowsAfter(child.SessionId, 0).Last(row => row.Kind == "turn").Payload.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_subagent_s_permission_is_a_card_in_its_parent_s_session_even_after_the_parent_s_turn_ended()
    {
        _host.Send("count the lines in the background");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync(ChildAnnounced);
        await agent.UpdateAsync(ChildPrompt, "child1");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await Until(() => _log.ToString().Contains("kept open"));

        await agent.RequestAsync(7, "session/request_permission",
            """{"sessionId":"child1","toolCall":{"toolCallId":"t9","title":"wc -l notes.txt"},"options":[{"optionId":"yes","name":"Allow","kind":"allow_once"}]}""");
        var card = await RowsUntil(row => row.Kind == "ask");

        Assert.Equal("general-purpose: wc -l notes.txt", card[^1].Payload.GetProperty("title").GetString());
        Assert.True(_host.Answer(card[^1].ItemId!["ask:".Length..], JsonDocument.Parse("""{"optionId":"yes"}""").RootElement));
        Assert.Equal("yes", (await agent.ResultAsync()).GetProperty("outcome").GetProperty("optionId").GetString());

        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"child1","state":{"state":"idle","stopReason":"end_turn"}}""");
        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
    }

    private async Task<(FakeLink Agent, string RunId)> StartClaudeBackgroundAsync()
    {
        var runId = _host.Send("start it")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"sleep 15","kind":"execute","status":"pending"}""");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"t1","status":"completed","rawOutput":"Command running in background with ID: b1. Output is being written to: /tmp/b1"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        var rows = await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");
        var task = Assert.Single(rows, row => row.Kind == "task");
        Assert.Equal(runId, task.RunId);
        Assert.Equal("""{"name":"Background command","state":"running","canStop":false,"toolCallId":"t1"}""", task.Payload.GetRawText());
        Assert.Equal("finished", rows[^1].Payload.GetProperty("state").GetString());
        await Until(() => _log.ToString().Contains("kept open"));
        return (agent, runId);
    }

}
