using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task A_close_that_never_answers_retires_its_adapter_and_the_next_turn_resumes_on_a_fresh_one()
    {
        _host.Send("one");
        var first = await _launcher.NextAsync();
        await first.StartAsync();
        var prompt = await first.ReadAsync("session/prompt");
        await first.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await first.ReadAsync("session/close");

        _time.Advance(SharedAdapter.CloseLimit);
        await Until(() => _host.ActiveRunId is null);
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

        _time.Advance(SharedAdapter.CloseLimit);
        await Until(() => _host.ActiveRunId is null);
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
        _host.Send("one");
        var first = await _launcher.NextAsync();
        await first.StartAsync();
        var prompt = await first.ReadAsync("session/prompt");
        other.Send("b");
        await first.OpenAsync("acp-2");
        var otherPrompt = await first.ReadAsync("session/prompt");

        await first.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await first.ReadAsync("session/close");
        _time.Advance(SharedAdapter.CloseLimit);
        await Until(() => _host.ActiveRunId is null);
        _host.Send("two");

        await first.TextAsync("still here", acpSessionId: "acp-2");
        await first.ReplyAsync(otherPrompt, """{"stopReason":"end_turn"}""");
        var close = await first.ReadAsync("session/close");
        Assert.False(first.Disposed);
        Assert.Equal(1, _launcher.Launches);
        await first.ReplyAsync(close, "{}");
        await Until(() => other.ActiveRunId is null);
        await Until(() => first.Disposed);

        var second = await _launcher.NextAsync();
        var resume = await second.StartAsync();
        Assert.Equal("session/resume", resume.GetProperty("method").GetString());
        await second.ReadAsync("session/prompt");
        await other.StopForShutdownAsync();
    }

    [Fact]
    public async Task Stopping_a_turn_that_waits_for_its_retired_adapter_ends_the_wait()
    {
        var other = Other();
        _host.Send("one");
        var first = await _launcher.NextAsync();
        await first.StartAsync();
        var prompt = await first.ReadAsync("session/prompt");
        other.Send("b");
        await first.OpenAsync("acp-2");
        var otherPrompt = await first.ReadAsync("session/prompt");

        await first.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await first.ReadAsync("session/close");
        _time.Advance(SharedAdapter.CloseLimit);
        await Until(() => _host.ActiveRunId is null);

        var runId = _host.Send("two")!;
        Assert.True(_host.Stop(runId));
        await Until(() => _host.ActiveRunId is null);
        Assert.Equal(1, _launcher.Launches);
        Assert.False(first.Disposed);

        await first.ReplyAsync(otherPrompt, """{"stopReason":"end_turn"}""");
        await first.CloseAsync();
        await Until(() => first.Disposed);
        await other.StopForShutdownAsync();
    }

    // A second session of the same agent, on the same shared adapter.
    private SessionHost Other() =>
        new(_sessionStore.Create("claude", "https://github.com/o/r").SessionId, _hub, _sessionStore, _opener, _time, _log);
}
