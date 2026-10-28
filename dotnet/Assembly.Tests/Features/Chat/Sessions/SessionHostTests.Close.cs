using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task A_close_that_never_answers_retires_its_adapter_and_the_next_turn_resumes_on_a_fresh_one()
    {
        var runId = _host.Send("one")!;
        var first = await _launcher.NextAsync();
        await first.StartAsync();
        var prompt = await first.ReadAsync("session/prompt");
        var closing = _time.WaitStarts(SharedAdapter.CloseLimit);
        await first.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await first.ReadAsync("session/close");

        await TimeOutClose(closing, runId);
        // No other session is on it, so it goes at once, not after the idle grace.
        await first.Exited.WaitAsync(Wait);
        Assert.True(first.Disposed);

        _host.Send("two");
        var second = await _launcher.NextAsync();
        var resume = await second.StartAsync();
        Assert.Equal("session/resume", resume.GetProperty("method").GetString());
        await second.ReadAsync("session/prompt");
    }

    [Fact]
    public async Task A_close_after_a_stopped_resume_that_never_answers_retires_its_adapter_and_the_next_turn_resumes_on_a_fresh_one()
    {
        var one = _host.Send("one")!;
        var first = await _launcher.NextAsync();
        await first.StartAsync();
        var prompt = await first.ReadAsync("session/prompt");
        await first.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await first.CloseAsync();
        await Kinds(4);
        Assert.Null(_host.ActiveRunId);
        // The first close is over once its run is logged.
        await _log.WrittenAsync($"run {one}: ");

        var runId = _host.Send("two")!;
        var held = await first.ReadAsync("session/resume");
        // The stop's background work first waits up to the close limit for the late answer; once that wait has
        // started, the next wait of that length is the close's.
        var waitingForAnswer = _time.WaitStarts(SharedAdapter.CloseLimit);
        Assert.True(_host.Stop(runId));
        await waitingForAnswer;
        var closing = _time.WaitStarts(SharedAdapter.CloseLimit);
        await first.ReplyAsync(held, "{}");
        await first.ReadAsync("session/close");

        await TimeOutClose(closing, runId);
        await first.Exited.WaitAsync(Wait);
        Assert.True(first.Disposed);

        _host.Send("three");
        var second = await _launcher.NextAsync();
        var resume = await second.StartAsync();
        Assert.Equal("session/resume", resume.GetProperty("method").GetString());
        await second.ReadAsync("session/prompt");
    }

    [Fact]
    public async Task Another_session_on_a_retired_adapter_finishes_its_turn_and_the_timed_out_session_resumes_only_once_it_has_exited()
    {
        var other = Other();
        var runId = _host.Send("one")!;
        var first = await _launcher.NextAsync();
        await first.StartAsync();
        var prompt = await first.ReadAsync("session/prompt");
        var otherRun = other.Send("b")!;
        await first.OpenAsync("acp-2");
        var otherPrompt = await first.ReadAsync("session/prompt");

        var closing = _time.WaitStarts(SharedAdapter.CloseLimit);
        await first.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await first.ReadAsync("session/close");
        await TimeOutClose(closing, runId);
        var second = new FakeLink();
        bool? launchedAfterExit = null;
        _launcher.Launch = _ =>
        {
            launchedAfterExit = first.Exited.IsCompleted;
            return Task.FromResult<IAgentLink>(second);
        };
        _host.Send("two");

        await first.TextAsync("still here", acpSessionId: "acp-2");
        await first.ReplyAsync(otherPrompt, """{"stopReason":"end_turn"}""");
        var close = await first.ReadAsync("session/close");
        Assert.False(first.Disposed);
        Assert.Equal(1, _launcher.Launches);
        await first.ReplyAsync(close, "{}");
        await _log.WrittenAsync($"run {otherRun}: ");
        Assert.Null(other.ActiveRunId);
        await first.Exited.WaitAsync(Wait);
        Assert.True(first.Disposed);

        var resume = await second.StartAsync();
        Assert.True(launchedAfterExit);
        Assert.Equal("session/resume", resume.GetProperty("method").GetString());
        await second.ReadAsync("session/prompt");
        await other.StopForShutdownAsync();
    }

    [Fact]
    public async Task A_stopped_turn_ends_its_wait_for_the_retired_adapter()
    {
        var other = Other();
        var runId = _host.Send("one")!;
        var first = await _launcher.NextAsync();
        await first.StartAsync();
        var prompt = await first.ReadAsync("session/prompt");
        other.Send("b");
        await first.OpenAsync("acp-2");
        var otherPrompt = await first.ReadAsync("session/prompt");

        var closing = _time.WaitStarts(SharedAdapter.CloseLimit);
        await first.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await first.ReadAsync("session/close");
        await TimeOutClose(closing, runId);

        // The other session keeps the retired adapter up, so the next open waits, until its turn is stopped.
        using var stop = new CancellationTokenSource();
        var waiting = _adapter.ClosedAsync(FakeLink.AcpSessionId, stop.Token);
        Assert.False(waiting.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(first.Disposed);

        await first.ReplyAsync(otherPrompt, """{"stopReason":"end_turn"}""");
        await first.CloseAsync();
        await first.Exited.WaitAsync(Wait);
        Assert.True(first.Disposed);
        await _adapter.ClosedAsync(FakeLink.AcpSessionId, CancellationToken.None);
        await other.StopForShutdownAsync();
    }

    // Neither adapter honours a request's cancel, so a session/new stopped after it went out is still opened there.
    [Fact]
    public async Task A_new_session_stopped_before_its_answer_is_closed_once_the_late_answer_arrives()
    {
        var runId = _host.Send("one")!;
        await Kinds(2);
        var agent = await _launcher.NextAsync();
        await agent.ReplyAsync(await agent.ReadAsync("initialize"), """{"protocolVersion":1,"agentCapabilities":{},"authMethods":[]}""");
        var open = await agent.ReadAsync("session/new");

        Assert.True(_host.Stop(runId));
        await agent.ReplyAsync(open, """{"sessionId":"acp-late"}""");

        Assert.Equal("acp-late", await agent.CloseAsync());
        Assert.Equal("cancelled", (await Rows(2))[1].Payload.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_new_session_stopped_while_it_is_being_named_is_closed()
    {
        var runId = _host.Send("one")!;
        await Kinds(2);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(canRename: true);
        await agent.ReadAsync("_session/rename");

        Assert.True(_host.Stop(runId));

        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
        Assert.Equal("cancelled", (await Rows(2))[1].Payload.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_resume_stopped_before_its_answer_is_closed_once_the_late_answer_arrives()
    {
        var (agent, resume) = await StoppedResumeAsync();

        await agent.ReplyAsync(resume, "{}");

        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
        Assert.Equal("cancelled", (await Rows(2))[1].Payload.GetProperty("state").GetString());
    }

    // The agent couldn't resume it, so nothing is loaded there to close, and the stop isn't held up by a close.
    [Fact]
    public async Task A_resume_stopped_before_its_answer_that_answers_with_an_error_is_not_closed()
    {
        var (agent, resume) = await StoppedResumeAsync();

        await agent.ReplyErrorAsync(resume, -32603, "Resource not found");

        Assert.Equal("cancelled", (await Rows(2))[1].Payload.GetProperty("state").GetString());
        _host.Send("three");
        await agent.ReadAsync("session/resume");
    }

    // The stop doesn't wait for the agent's late answer: the turn ends cancelled while session/new is still out.
    [Fact]
    public async Task A_new_session_stopped_before_its_answer_ends_the_turn_cancelled_at_once()
    {
        await StoppedNewAsync();

        var end = (await RowsThrough(row => row.Kind == "turn"))[^1];
        Assert.Equal("cancelled", end.Payload.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_send_right_after_a_stopped_new_session_goes_through_while_the_late_answer_is_still_out()
    {
        var (agent, _) = await StoppedNewAsync();
        await RowsThrough(row => row.Kind == "turn");

        Assert.NotNull(_host.Send("two"));
        await agent.ReadAsync("session/new");
    }

    [Fact]
    public async Task A_late_answer_after_the_stopped_turn_ended_is_closed_before_the_adapter_is_shut_down()
    {
        var (agent, open) = await StoppedNewAsync();
        await RowsThrough(row => row.Kind == "turn");

        await agent.ReplyAsync(open, """{"sessionId":"acp-late"}""");

        var close = await agent.ReadAsync("session/close");
        Assert.Equal("acp-late", close.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.False(agent.Disposed);
        await agent.ReplyAsync(close, "{}");
    }

    [Fact]
    public async Task A_resume_stopped_before_its_answer_ends_the_turn_cancelled_at_once()
    {
        await StoppedResumeAsync();

        var end = (await RowsThrough(row => row.Kind == "turn"))[^1];
        Assert.Equal("cancelled", end.Payload.GetProperty("state").GetString());
    }

    // The stopped resume's late answer is for the same ACP session the next send resumes, so the next send resumes it
    // only once that late answer's close is done; otherwise the close would end the new turn's session.
    [Fact]
    public async Task A_send_after_a_stopped_resume_resumes_only_once_the_late_answer_is_closed()
    {
        var (agent, resume) = await StoppedResumeAsync();
        await RowsThrough(row => row.Kind == "turn");
        Assert.NotNull(_host.Send("three"));
        await Kinds(2);

        // Nothing reaches the agent while the late answer is still owed: the next open waits for it, so a second
        // resume that went out at once shows up here. (The next open has no event of its own to wait on.)
        var next = agent.NextMessageAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => next.WaitAsync(TimeSpan.FromMilliseconds(200)));
        await agent.ReplyAsync(resume, "{}");

        // The agent's next message is the late answer's close, not a second resume.
        var close = await next.WaitAsync(Wait);
        Assert.Equal("session/close", close.GetProperty("method").GetString());
        await agent.ReplyAsync(close, "{}");
        await agent.ReplyAsync(await agent.ReadAsync("session/resume"), "{}");
        // No close for the session while its turn is open: its prompt is the next message.
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        Assert.Equal("finished", (await RowsThrough(row => row.Kind == "turn"))[^1].Payload.GetProperty("state").GetString());
        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
    }

    // A first turn's session/new, held by the agent, and that turn stopped.
    private async Task<(FakeLink Agent, JsonElement Open)> StoppedNewAsync()
    {
        var runId = _host.Send("one")!;
        await Kinds(2);
        var agent = await _launcher.NextAsync();
        await agent.ReplyAsync(await agent.ReadAsync("initialize"), """{"protocolVersion":1,"agentCapabilities":{},"authMethods":[]}""");
        var open = await agent.ReadAsync("session/new");
        Assert.True(_host.Stop(runId));
        return (agent, open);
    }

    // A second turn's resume, held by the agent, and that turn stopped.
    private async Task<(FakeLink Agent, JsonElement Resume)> StoppedResumeAsync()
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await Kinds(4);
        Assert.Null(_host.ActiveRunId);

        var runId = _host.Send("two")!;
        await Kinds(2);
        var resume = await agent.ReadAsync("session/resume");
        Assert.True(_host.Stop(runId));
        return (agent, resume);
    }

    // An update pump that never ends holds the adapter's dispose only up to the close limit, and is logged.
    [Fact]
    public async Task A_stuck_update_pump_holds_the_dispose_only_to_the_close_limit()
    {
        var (adapter, link, _) = await HeldAdapterAsync();

        var pumpWait = _time.WaitStarts(SharedAdapter.CloseLimit);
        var disposing = adapter.DisposeAsync().AsTask();
        await pumpWait;
        _time.Advance(SharedAdapter.CloseLimit);
        await disposing.WaitAsync(Wait);
        Assert.Contains("shutdown: update pump for claude still running after 5 s", _log.ToString());
        await link.DisposeAsync();
    }

    // A retired adapter's last release waits for its update pump too, up to the same limit.
    [Fact]
    public async Task A_retired_adapters_last_release_waits_for_its_update_pump()
    {
        var (adapter, link, running) = await HeldAdapterAsync();
        var closeWait = _time.WaitStarts(SharedAdapter.CloseLimit);
        var closing = adapter.CloseAsync(running, FakeLink.AcpSessionId);
        await link.ReadAsync("session/close");
        await closeWait;
        _time.Advance(SharedAdapter.CloseLimit);
        await Task.WhenAny(closing).WaitAsync(Wait);
        await Assert.ThrowsAsync<TimeoutException>(() => closing);

        var pumpWait = _time.WaitStarts(SharedAdapter.CloseLimit);
        var releasing = adapter.ReleaseAsync(running);
        await pumpWait;
        _time.Advance(SharedAdapter.CloseLimit);
        await releasing.WaitAsync(Wait);
        Assert.Contains("shutdown: update pump for claude still running after 5 s", _log.ToString());
        await adapter.DisposeAsync();
        await link.DisposeAsync();
    }

    // An adapter, with one session on it, whose link's dispose doesn't end its updates, so its pump never ends.
    private async Task<(SharedAdapter, FakeLink, RunningAdapter)> HeldAdapterAsync()
    {
        var link = new FakeLink();
        var launcher = new FakeLauncher { Launch = _ => Task.FromResult<IAgentLink>(new HeldLink(link)) };
        var adapter = new SharedAdapter(launcher, "claude", _time, _log);
        var getting = adapter.GetAsync(new StageTimes(_time), CancellationToken.None);
        await link.ReplyAsync(await link.ReadAsync("initialize"), """{"protocolVersion":1,"agentCapabilities":{},"authMethods":[]}""");
        return (adapter, link, await getting.WaitAsync(Wait));
    }

    private sealed class HeldLink(FakeLink inner) : IAgentLink
    {
        public AcpClient Client => inner.Client;

        public Task Exited => inner.Exited;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Moves the clock past the close's limit once the close's wait has started, and waits for the run to be logged as
    // done, which is after its close. (The run is no longer active even before its close, so that can't be what it
    // waits for.)
    private async Task TimeOutClose(Task closeWait, string runId)
    {
        await closeWait;
        _time.Advance(SharedAdapter.CloseLimit);
        await _log.WrittenAsync($"run {runId}: ");
    }

    // The tests' clock: frozen unless a test moves it, and it says when a wait of a given length starts on it, so a
    // test moves it only once the wait it means is there.
    private sealed class WatchedClock() : FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000))
    {
        private readonly List<(TimeSpan Length, TaskCompletionSource Started)> _watches = [];

        // Done when the next wait of this length starts.
        public Task WaitStarts(TimeSpan length)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_watches)
                _watches.Add((length, started));
            return started.Task.WaitAsync(Wait);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            lock (_watches)
            {
                foreach (var watch in _watches.Where(watch => watch.Length == dueTime))
                    watch.Started.TrySetResult();
                _watches.RemoveAll(watch => watch.Length == dueTime);
            }
            return timer;
        }
    }

    // A second session of the same agent, on the same shared adapter.
    private SessionHost Other() =>
        new(_sessionStore.Create("claude", "https://github.com/o/r").SessionId, _hub, _sessionStore, _opener, _time, _log, _work);
}
