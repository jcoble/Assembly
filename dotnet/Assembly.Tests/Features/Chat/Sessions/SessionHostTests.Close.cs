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
        await first.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await first.ReadAsync("session/close");

        await TimeOutClose(runId);
        // No other session is on it, so it goes at once, not after the idle grace.
        await Until(() => first.Disposed);

        _host.Send("two");
        var second = await _launcher.NextAsync();
        var resume = await second.StartAsync();
        Assert.Equal("session/resume", resume.GetProperty("method").GetString());
        await second.ReadAsync("session/prompt");
    }

    [Fact]
    public async Task A_close_after_a_stopped_resume_that_never_answers_retires_its_adapter_and_the_next_turn_resumes_on_a_fresh_one()
    {
        _host.Send("one");
        var first = await _launcher.NextAsync();
        await first.StartAsync();
        var prompt = await first.ReadAsync("session/prompt");
        await first.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await first.CloseAsync();
        await Until(() => _host.ActiveRunId is null);

        var runId = _host.Send("two")!;
        await first.ReadAsync("session/resume");
        Assert.True(_host.Stop(runId));
        await first.ReadAsync("session/close");

        await TimeOutClose(runId);
        await Until(() => first.Disposed);

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
        other.Send("b");
        await first.OpenAsync("acp-2");
        var otherPrompt = await first.ReadAsync("session/prompt");

        await first.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await first.ReadAsync("session/close");
        await TimeOutClose(runId);
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
        await Until(() => other.ActiveRunId is null);
        await Until(() => first.Disposed);

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

        await first.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await first.ReadAsync("session/close");
        await TimeOutClose(runId);

        // The other session keeps the retired adapter up, so the next open waits, until its turn is stopped.
        using var stop = new CancellationTokenSource();
        var waiting = _adapter.ClosedAsync(FakeLink.AcpSessionId, stop.Token);
        Assert.False(waiting.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(first.Disposed);

        await first.ReplyAsync(otherPrompt, """{"stopReason":"end_turn"}""");
        await first.CloseAsync();
        await Until(() => first.Disposed);
        await _adapter.ClosedAsync(FakeLink.AcpSessionId, CancellationToken.None);
        await other.StopForShutdownAsync();
    }

    // An update pump that never ends holds the adapter's dispose only up to the close limit, and is logged.
    [Fact]
    public async Task A_stuck_update_pump_holds_the_dispose_only_to_the_close_limit()
    {
        var (adapter, link, _) = await HeldAdapterAsync();

        var disposing = adapter.DisposeAsync().AsTask();
        await Until(() =>
        {
            _time.Advance(SharedAdapter.CloseLimit);
            return disposing.IsCompleted;
        });
        await disposing;
        Assert.Contains("shutdown: update pump for claude still running after 5 s", _log.ToString());
        await link.DisposeAsync();
    }

    // A retired adapter's last release waits for its update pump too, up to the same limit.
    [Fact]
    public async Task A_retired_adapters_last_release_waits_for_its_update_pump()
    {
        var (adapter, link, running) = await HeldAdapterAsync();
        var closing = adapter.CloseAsync(running, FakeLink.AcpSessionId);
        await link.ReadAsync("session/close");
        await Until(() =>
        {
            _time.Advance(SharedAdapter.CloseLimit);
            return closing.IsCompleted;
        });
        await Assert.ThrowsAsync<TimeoutException>(() => closing);

        var releasing = adapter.ReleaseAsync(running);
        await Until(() =>
        {
            _time.Advance(SharedAdapter.CloseLimit);
            return releasing.IsCompleted;
        });
        await releasing;
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

    // Moves the clock past the close's limit until the run is logged as done, which is after its close. Once is enough
    // unless the limit's timer started after the test saw session/close, so it moves again on each check. (The run is
    // no longer active even before its close, so that can't be what it waits for.)
    private Task TimeOutClose(string runId) =>
        Until(() =>
        {
            _time.Advance(SharedAdapter.CloseLimit);
            return _log.ToString().Contains($"run {runId}: ");
        });

    // A second session of the same agent, on the same shared adapter.
    private SessionHost Other() =>
        new(_sessionStore.Create("claude", "https://github.com/o/r").SessionId, _hub, _sessionStore, _opener, _time, _log, _work);
}
