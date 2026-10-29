using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Draft.Remote;

public sealed partial class RemoteRowsSocketTests
{
    // Pins: remote#12, remote#22
    [Fact]
    public async Task Remote_session_is_created_and_sent_a_prompt_over_the_machine_socket()
    {
        await _machine.ProbeAsync(CancellationToken.None);
        var session = await _machine.CreateSessionAsync("claude", "https://github.com/o/r", CancellationToken.None);
        Assert.Equal("run-1", await _machine.SendAsync(session, "hello", "client-1", CancellationToken.None));
        Assert.Contains($"send {session.SessionId} hello", _remoteMachine.Asked);
        Assert.Equal("client-1", _remoteMachine.SentClientId);
        Assert.Equal(0, _sessionHttpHits);
    }

    // Pins: remote#23, remote#24
    [Fact]
    public async Task Remote_stop_and_answer_reach_the_remote_session_over_the_machine_socket()
    {
        var batch = new RowBatch();
        batch.Rows.Add(new NewRow("running", null, "turn", JsonDocument.Parse("""{"state":"started"}""").RootElement.Clone(), 1));
        _remoteHub.Publish("s", batch);
        await _machine.ProbeAsync(CancellationToken.None);
        Assert.True(await _machine.StopAsync(_localSessions.Get("s")!, "running", CancellationToken.None));
        _remoteMachine.Refuses = true;
        Assert.False(await _machine.AnswerAsync(_localSessions.Get("s")!, "gone",
            JsonDocument.Parse("""{"optionId":"yes"}""").RootElement, CancellationToken.None));
        Assert.Contains("stop running", _remoteMachine.Asked);
        Assert.Contains(_remoteMachine.Asked, call => call.StartsWith("answer gone "));
        Assert.Equal(0, _sessionHttpHits);
    }

    // Pins: remote#16, remote#21
    [Fact]
    public async Task Remote_session_title_arrives_as_a_sessions_push_without_an_http_refresh()
    {
        await _machine.ProbeAsync(CancellationToken.None);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = _localTopics.Subscribe(SessionsFeature.Topic, "s", _ => { }, (_, _) =>
        {
            if (_localSessions.Get("s") is { } session && session.Title == "A turn started on the box"
                && _localSessions.Item(session).ActiveRunId == "direct-run") changed.TrySetResult();
        });
        try
        {
            _remoteSessions.SetTitle("s", "A turn started on the box", "helper");
            var batch = new RowBatch();
            batch.Rows.Add(new NewRow("direct-run", null, "turn", JsonDocument.Parse("""{"state":"started"}""").RootElement.Clone(), 1));
            _remoteHub.Publish("s", batch);
            await changed.Task.WaitAsync(Wait);
            Assert.Equal("helper", _localSessions.Get("s")!.TitleSource);
            Assert.Equal(0, _sessionHttpHits);
        }
        finally { _localTopics.Unsubscribe(subscription); }
    }

    // Pins: remote#11
    [Fact]
    public async Task A_remote_with_another_row_format_is_refused_at_welcome()
    {
        _welcomeFormat = RowStore.FormatVersion + 1;
        var error = await Assert.ThrowsAsync<MachineUnreachable>(() => _machine.ProbeAsync(CancellationToken.None));
        Assert.Contains("box", error.Message);
        Assert.Contains($"format {_welcomeFormat}", error.Message);
        Assert.Contains($"format {RowStore.FormatVersion}", error.Message);
        Assert.False(_status.Items.Single(item => item.Name == "box").Reachable);
    }

    // Pins: remote#30. A lost connection cannot replay an in-flight call.
    [Fact]
    public async Task Losing_the_machine_socket_fails_a_pending_call()
    {
        await _machine.ProbeAsync(CancellationToken.None);
        _remoteMachine.Hold();
        var call = _machine.ConfigAsync(_localSessions.Get("s")!, CancellationToken.None);
        await _remoteMachine.Reached.Task.WaitAsync(Wait);
        _socket.DropAll();
        await Assert.ThrowsAsync<MachineUnreachable>(() => call.WaitAsync(Wait));
        _remoteMachine.Release();
        Assert.Single(_remoteMachine.Asked, asked => asked == "config");
    }

    [Fact]
    public async Task Cancelling_a_machine_call_cancels_the_remote_read()
    {
        await _machine.ProbeAsync(CancellationToken.None);
        _remoteMachine.Hold();
        using var stop = new CancellationTokenSource();
        var call = _machine.ConfigAsync(_localSessions.Get("s")!, stop.Token);
        await _remoteMachine.Reached.Task.WaitAsync(Wait);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await _remoteMachine.Cancelled.Task.WaitAsync(Wait);
    }

    // Pins: remote#30
    [Fact]
    public async Task A_call_while_the_socket_is_closed_fails_unreachable_at_once()
    {
        var call = _machine.CreateSessionAsync("claude", "https://github.com/o/r", CancellationToken.None);
        Assert.True(call.IsCompleted);
        await Assert.ThrowsAsync<MachineUnreachable>(() => call);
        Assert.Equal(0, _sessionHttpHits);
    }
}
