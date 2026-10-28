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

// SessionHostTests, continued: sessions opening, resuming and closing, the shared adapter, stopping and failing.
public sealed partial class SessionHostTests
{
    [Fact]
    public async Task SteerWhileOpeningWaitsForTheSession()
    {
        _sessionStore.SetConfigChoice(_session.SessionId, "model", "haiku");
        var runId = _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options("opus", "low"));
        var model = await agent.ReadAsync("session/set_config_option");

        var steering = _host.SteerAsync("two", null, CancellationToken.None);
        Assert.False(steering.IsCompleted);
        await agent.ReplyAsync(model, $$"""{"configOptions":{{Options("haiku", "low")}}}""");

        // The initial prompt and the waiting steer both follow the open, in either order.
        var next = new[] { await agent.NextMessageAsync().WaitAsync(Wait), await agent.NextMessageAsync().WaitAsync(Wait) };
        var prompt = next.Single(message => message.GetProperty("method").GetString() == "session/prompt");
        var steer = next.Single(message => message.GetProperty("method").GetString() == "_session/steering");
        Assert.Equal(FakeLink.AcpSessionId, steer.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal("two", steer.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await agent.ReplyAsync(steer, """{"outcome":"injected"}""");
        Assert.Equal(runId, await steering.WaitAsync(Wait));
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();

        var saved = Assert.Single(_store.RowsAfter(_session.SessionId, 0),
            row => row.Kind == "user" && row.Payload.GetProperty("text").GetString() == "two");
        Assert.Equal(runId, saved.RunId);
        Assert.Equal(1, _launcher.Launches);
    }

    [Fact]
    public async Task SteerWhileOpeningHonoursCancellation()
    {
        _sessionStore.SetConfigChoice(_session.SessionId, "model", "haiku");
        var runId = _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options("opus", "low"));
        var model = await agent.ReadAsync("session/set_config_option");

        using var cancel = new CancellationTokenSource();
        var steering = _host.SteerAsync("two", null, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => steering.WaitAsync(Wait));

        await agent.ReplyAsync(model, $$"""{"configOptions":{{Options("haiku", "low")}}}""");
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal(runId, _host.ActiveRunId);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        // A stray steer would be read here instead of the close.
        await agent.CloseAsync();
        Assert.Single(_store.RowsAfter(_session.SessionId, 0), row => row.Kind == "user");
        Assert.Equal(1, _launcher.Launches);
    }

    [Fact]
    public async Task SteerAfterAFailedOpenIsRefused()
    {
        var launching = new TaskCompletionSource<IAgentLink>();
        _launcher.Launch = _ => launching.Task;
        var runId = _host.Send("one");
        var steering = _host.SteerAsync("two", null, CancellationToken.None);
        launching.SetException(new InvalidOperationException("open failed"));

        Assert.Null(await steering.WaitAsync(Wait));
        var rows = await Rows(5);
        Assert.Equal(["user", "turn", "error", "stats", "turn"], rows.Select(row => row.Kind));
        Assert.Equal("failed", rows[4].Payload.GetProperty("state").GetString());
        Assert.All(rows, row => Assert.Equal(runId, row.RunId));
        Assert.Null(_host.ActiveRunId);
        Assert.Single(_store.RowsAfter(_session.SessionId, 0), row => row.Kind == "user");
        Assert.Equal(1, _launcher.Launches);
        Assert.Single(_launcher.CheckedOut);
    }

    [Fact]
    public async Task An_agent_that_does_not_steer_refuses_a_message_mid_turn()
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(canSteer: false);
        await agent.ReadAsync("session/prompt");

        Assert.Null(await _host.SteerAsync("two", null, CancellationToken.None));
    }

    [Fact]
    public async Task An_update_that_comes_after_its_session_closed_never_reaches_the_next_turn()
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await Kinds(4);
        await Until(() => _host.ActiveRunId is null);

        // The same ACP session is resumed, so the closed turn's late update shares its id with the next turn's. It
        // arrives after the resume request, before the answer.
        _host.Send("two");
        var resume = await agent.ReadAsync("session/resume");
        await agent.TextAsync("late", messageId: "late");
        await agent.ReplyAsync(resume, "{}");
        var second = await agent.ReadAsync("session/prompt");
        await agent.TextAsync("fresh", messageId: "fresh");
        await agent.ReplyAsync(second, """{"stopReason":"end_turn"}""");

        var rows = await Rows(6);
        Assert.Equal(["user", "turn", "delta", "message", "stats", "turn"], rows.Select(row => row.Kind));
        Assert.All(rows.Where(row => row.ItemId is not null), row => Assert.Equal("fresh", row.ItemId));
    }

    [Fact]
    public async Task An_update_sent_right_after_the_resume_answer_reaches_the_next_turn()
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await Kinds(4);
        await Until(() => _host.ActiveRunId is null);

        // The resumed session's own update, sent the moment it answers, as claude-agent-acp sends its commands. This
        // can't force the client's threads into the losing order; AcpClientTests pins the order itself.
        _host.Send("two");
        var resume = await agent.ReadAsync("session/resume");
        await agent.ReplyThenTextAsync(resume, "{}", "early", "early");
        var second = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(second, """{"stopReason":"end_turn"}""");

        var rows = await Rows(6);
        Assert.Contains(rows, row => row.ItemId == "early");
    }

    [Fact]
    public async Task A_resume_stopped_before_its_session_is_ready_is_closed_and_its_updates_never_reach_a_turn()
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options("opus", "low"));
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await Kinds(4);
        await Until(() => _host.ActiveRunId is null);
        await _host.SetConfigAsync("mode", "plan", CancellationToken.None);

        // The resume succeeds, then the turn is stopped while the session's choices are still being applied.
        var runId = _host.Send("two")!;
        var resume = await agent.ReadAsync("session/resume");
        await agent.ReplyAsync(resume, $$"""{"configOptions":{{Options("opus", "low")}}}""");
        await agent.ReadAsync("session/set_config_option");
        Assert.True(_host.Stop(runId));
        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
        await agent.TextAsync("stray", messageId: "stray");
        await Until(() => _host.ActiveRunId is null);

        _host.Send("three");
        var third = await agent.ReadAsync("session/resume");
        await agent.ReplyAsync(third, $$"""{"configOptions":{{Options("opus", "low", "plan")}}}""");
        var thirdPrompt = await agent.ReadAsync("session/prompt");
        await agent.TextAsync("fresh", messageId: "fresh");
        await agent.ReplyAsync(thirdPrompt, """{"stopReason":"end_turn"}""");

        var rows = await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "finished");
        Assert.Contains(rows, row => row.ItemId == "fresh");
        Assert.DoesNotContain(rows, row => row.ItemId == "stray");
    }

    [Fact]
    public async Task The_adapter_stops_once_it_has_had_no_session_for_the_idle_grace()
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await Until(() => _host.ActiveRunId is null);
        // The close's answer is handled before the clock moves, or the jump would time the close out.
        await Until(() => _log.ToString().Contains("session close"));

        // Inside the grace the next turn finds it warm.
        _time.Advance(SharedAdapter.IdleGrace - TimeSpan.FromSeconds(1));
        _host.Send("two");
        await agent.OpenAsync();
        var second = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(second, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await Until(() => _host.ActiveRunId is null);
        await Until(() => _log.ToString().Split("session close").Length == 3);
        Assert.False(agent.Disposed);

        // The grace starts once the close's answer is in, so the clock moves on in steps until the adapter stops.
        var waited = TimeSpan.Zero;
        await Until(() =>
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            waited += TimeSpan.FromSeconds(5);
            return agent.Disposed;
        });
        Assert.True(waited >= SharedAdapter.IdleGrace);
        Assert.Equal(1, _launcher.Launches);
    }

    [Fact]
    public async Task Text_held_back_goes_out_on_the_next_clock_tick()
    {
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        await Kinds(2);

        await agent.TextAsync("a");
        var first = (await Rows(1))[0];
        Assert.Equal(1_000_000, first.CreatedAt);

        _time.Advance(TimeSpan.FromMilliseconds(10));
        await agent.TextAsync("b");
        // The title is a marker: once it's saved, "b" has been read and held.
        await agent.UpdateAsync("""{"sessionUpdate":"session_info_update","title":"marker"}""");
        await Until(() => _sessionStore.Get(_session.SessionId)!.Title == "marker");
        Assert.False(_live.Rows.TryRead(out _));

        _time.Advance(TimeSpan.FromMilliseconds(100));
        var held = (await Rows(1))[0];
        Assert.Equal("delta", held.Kind);
        Assert.Equal("b", held.Payload.GetProperty("text").GetString());
        Assert.Equal(1_000_110, held.CreatedAt);
    }

    [Fact]
    public async Task An_agent_that_dies_mid_turn_ends_it_and_the_next_send_resumes_its_session()
    {
        var runId = _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"Terminal","kind":"execute","status":"pending","rawInput":{"command":"sleep 60"}}""");
        await Kinds(3);

        agent.Crash();
        var end = await Rows(4);
        Assert.Equal(["tool", "error", "stats", "turn"], end.Select(row => row.Kind));
        Assert.Equal("failed", end[0].Payload.GetProperty("state").GetString());
        Assert.Equal("agent_lost", end[1].Payload.GetProperty("code").GetString());
        Assert.Equal("failed", end[3].Payload.GetProperty("state").GetString());
        Assert.Null(_host.ActiveRunId);

        // The next send finds the adapter gone, ends it, and starts a new one, which resumes the session.
        Assert.NotNull(_host.Send("again"));
        var second = await _launcher.NextAsync();
        var open = await second.StartAsync();
        Assert.Equal("session/resume", open.GetProperty("method").GetString());
        Assert.Equal(FakeLink.AcpSessionId, open.GetProperty("params").GetProperty("sessionId").GetString());
        await second.ReadAsync("session/prompt");
        Assert.True(agent.Disposed);
        Assert.Equal(2, _launcher.Launches);
    }

    [Fact]
    public async Task Each_turn_resumes_the_closed_session_and_costs_what_its_total_grew_past_the_saved_one()
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await CostAsync(agent, 0.20m);
        Assert.Equal(0.20m, (await Stats()).GetProperty("costUsd").GetDecimal());
        await agent.CloseAsync();
        await Until(() => _sessionStore.Get(_session.SessionId)!.SessionCost == 0.20m);

        // The same adapter: no new launch and no initialize, just the session reopened. The agent reports its
        // total on from where the closed session left it.
        _host.Send("two");
        var open = await agent.OpenAsync();
        Assert.Equal("session/resume", open.GetProperty("method").GetString());
        await CostAsync(agent, 0.32m);
        Assert.Equal(0.12m, (await Stats()).GetProperty("costUsd").GetDecimal());
        await agent.CloseAsync();
        await Until(() => _sessionStore.Get(_session.SessionId)!.SessionCost == 0.32m);
        Assert.Equal(1, _launcher.Launches);
    }

    [Fact]
    public async Task A_resumed_turn_without_a_saved_total_has_no_cost()
    {
        _sessionStore.SetAcpSession(_session.SessionId, "earlier");
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        Assert.Equal("session/resume", (await agent.StartAsync()).GetProperty("method").GetString());
        await CostAsync(agent, 0.25m);
        Assert.False((await Stats()).TryGetProperty("costUsd", out _));
    }

    [Fact]
    public async Task Sessions_share_one_adapter_initialized_once_and_each_gets_its_own_updates()
    {
        var other = _sessionStore.Create("claude", "https://github.com/o/r");
        var otherHost = new SessionHost(other.SessionId, _hub, _sessionStore, _opener, _time, _log, _work);
        using var otherLive = _hub.Subscribe(other.SessionId, after: 0);

        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(acpSessionId: "acp-1");
        var first = await agent.ReadAsync("session/prompt");
        otherHost.Send("two");
        await agent.OpenAsync("acp-2");
        var second = await agent.ReadAsync("session/prompt");
        Assert.Equal("acp-2", second.GetProperty("params").GetProperty("sessionId").GetString());

        await agent.TextAsync("for one", acpSessionId: "acp-1");
        await agent.TextAsync("for two", acpSessionId: "acp-2");
        await agent.ReplyAsync(second, """{"stopReason":"end_turn"}""");
        await agent.ReplyAsync(first, """{"stopReason":"end_turn"}""");

        Assert.Equal(1, _launcher.Launches);
        Assert.Equal("for one", await MessageTextAsync(_live));
        Assert.Equal("for two", await MessageTextAsync(otherLive));
        Assert.Equal(["acp-1", "acp-2"], new[] { await agent.CloseAsync(), await agent.CloseAsync() }.Order());
        await otherHost.StopForShutdownAsync();
    }

    [Fact]
    public async Task When_the_shared_adapter_dies_every_turn_on_it_ends_as_lost()
    {
        var other = _sessionStore.Create("claude", "https://github.com/o/r");
        var otherHost = new SessionHost(other.SessionId, _hub, _sessionStore, _opener, _time, _log, _work);
        using var otherLive = _hub.Subscribe(other.SessionId, after: 0);
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(acpSessionId: "acp-1");
        await agent.ReadAsync("session/prompt");
        otherHost.Send("two");
        await agent.OpenAsync("acp-2");
        await agent.ReadAsync("session/prompt");

        agent.Crash();
        Assert.Equal("agent_lost", await ErrorCodeAsync(_live));
        Assert.Equal("agent_lost", await ErrorCodeAsync(otherLive));
        await otherHost.StopForShutdownAsync();
    }

    [Fact]
    public async Task An_agent_that_cannot_resume_gets_a_new_session()
    {
        _sessionStore.SetAcpSession(_session.SessionId, "old");
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        var open = await agent.StartAsync(canResume: false);

        Assert.Equal("session/new", open.GetProperty("method").GetString());
        await agent.ReadAsync("session/prompt");
        Assert.Equal(FakeLink.AcpSessionId, _sessionStore.Get(_session.SessionId)!.AcpSessionId);
    }

    [Fact]
    public async Task Stop_cancels_the_running_turn()
    {
        var runId = _host.Send("hi")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await Kinds(2);

        Assert.False(_host.Stop("some-other-run"));
        Assert.True(_host.Stop(runId));
        var cancel = await agent.ReadAsync("session/cancel");
        Assert.Equal(FakeLink.AcpSessionId, cancel.GetProperty("params").GetProperty("sessionId").GetString());
        await agent.ReplyAsync(prompt, """{"stopReason":"cancelled"}""");

        var end = await Rows(2);
        Assert.Equal("aborted", end[0].Payload.GetProperty("status").GetString());
        Assert.Equal("cancelled", end[1].Payload.GetProperty("state").GetString());
    }

    // The tracker holds a stop's cancel: one that fails is logged by its name, and it still ends.
    [Fact]
    public async Task A_stops_cancel_that_fails_is_logged_and_ends()
    {
        var runId = _host.Send("hi")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        await Kinds(2);
        agent.BreakInput();

        Assert.True(_host.Stop(runId));
        await Until(() => _log.ToString().Contains($"background cancel {runId}: "));
        await Until(() => _work.Running == 0);
    }

    // Inside the idle grace, disposing the adapter ends its idle wait and waits for it. The idle wait's end is held on
    // a context the test releases, so the dispose returns only if it awaits the wait.
    [Fact]
    public async Task Disposing_the_adapter_inside_its_idle_grace_waits_for_the_idle_wait()
    {
        var getting = _adapter.GetAsync(new StageTimes(_time), CancellationToken.None);
        var agent = await _launcher.NextAsync();
        await agent.ReplyAsync(await agent.ReadAsync("initialize"), """{"protocolVersion":1,"agentCapabilities":{},"authMethods":[]}""");
        var running = await getting.WaitAsync(Wait);
        var held = new HeldContext();
        var outer = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(held);
        var releasing = _adapter.ReleaseAsync(running);
        SynchronizationContext.SetSynchronizationContext(outer);
        Assert.True(releasing.IsCompleted);

        var disposing = _adapter.DisposeAsync().AsTask();
        Assert.False(agent.Disposed);
        held.Release();
        await disposing.WaitAsync(Wait);
        Assert.True(agent.Disposed);
    }

    // Holds what is posted to it until Release runs it.
    private sealed class HeldContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _posted = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_posted)
                _posted.Add((d, state));
        }

        public void Release()
        {
            (SendOrPostCallback Callback, object? State)[] posted;
            lock (_posted)
            {
                posted = [.. _posted];
                _posted.Clear();
            }
            foreach (var (callback, state) in posted)
                callback(state);
        }
    }

    [Fact]
    public async Task Stop_before_the_agent_is_up_cancels_without_a_prompt()
    {
        var launching = new TaskCompletionSource<IAgentLink>();
        _launcher.Launch = cancellationToken =>
        {
            cancellationToken.Register(() => launching.TrySetCanceled(cancellationToken));
            return launching.Task;
        };
        var runId = _host.Send("hi")!;
        await Kinds(2);

        Assert.True(_host.Stop(runId));
        var end = await Rows(2);
        Assert.Equal(["stats", "turn"], end.Select(row => row.Kind));
        Assert.Equal("cancelled", end[1].Payload.GetProperty("state").GetString());
        Assert.Null(_host.ActiveRunId);
    }

    [Fact]
    public async Task A_failed_launch_fails_the_turn_with_its_reason()
    {
        _launcher.Launch = _ => throw new InvalidOperationException("git clone failed: repository not found");
        _host.Send("hi");

        var rows = await Rows(5);
        Assert.Equal(["user", "turn", "error", "stats", "turn"], rows.Select(row => row.Kind));
        Assert.Equal("git clone failed: repository not found", rows[2].Payload.GetProperty("message").GetString());
        Assert.Equal("failed", rows[4].Payload.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_prompt_error_fails_the_turn()
    {
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyErrorAsync(prompt, -32603, "Internal error");

        var rows = await Rows(5);
        Assert.Equal(["user", "turn", "error", "stats", "turn"], rows.Select(row => row.Kind));
        Assert.Equal("-32603", rows[2].Payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_agents_title_goes_to_the_sessions_list()
    {
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"session_info_update","title":"Say hello"}""");

        await Until(() => _sessionStore.Get(_session.SessionId)!.Title == "Say hello");
    }

    [Fact]
    public async Task The_agents_slash_commands_are_saved_for_the_session()
    {
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"context","description":"Show context usage","input":null}]}""");

        await Until(() => _sessionStore.Config(_session.SessionId).Commands?.Contains("\"context\"") == true);
    }
}
