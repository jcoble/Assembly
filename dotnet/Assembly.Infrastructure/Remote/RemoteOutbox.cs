using Assembly.Application.Features.Chat.Helpers;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Infrastructure.Remote;

/// <summary>
/// Messages a remote's helper sessions send to their parents here, which the remote can't push: the Mac picks them up
/// every second while a helper of a session here has a run open there and once more after it ends, and every few
/// seconds otherwise while the remote has helpers of sessions here, so a helper that sends and ends between two looks
/// isn't missed. It delivers each to its session (a prompt, or steered into the running turn), and tells the remote
/// the run; when that run ends it brings the answer back.
/// Each message is recorded here before it's delivered, so it's delivered once, even across a restart.
/// </summary>
internal sealed class RemoteOutbox : IAsyncDisposable
{
    private readonly RemoteMachine _remote;
    private readonly SessionStore _sessionStore;
    private readonly LiveHub _hub;
    private readonly IMachine _local;
    private readonly TextWriter _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _polling;
    private bool _wasWorking;
    private int _idleTicks;

    public RemoteOutbox(RemoteMachine remote, SessionStore sessionStore, LiveHub hub, IMachine local, TextWriter log)
    {
        _remote = remote;
        _sessionStore = sessionStore;
        _hub = hub;
        _local = local;
        _log = log;
        _polling = Task.Run(PollAsync);
    }

    private async Task PollAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _stopping.Token);
                try
                {
                    await PollOnceAsync(_stopping.Token);
                }
                catch (MachineUnreachable error)
                {
                    _log.WriteLine($"{_remote.Name} outbox: {error.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var helpers = _sessionStore.HelpersOn(_remote.Name!);
        var working = helpers.Any(helper => _hub.OpenRun(helper.SessionId) is not null);
        var look = working || _wasWorking || (helpers.Count > 0 && ++_idleTicks >= IdleTicks);
        _wasWorking = working;
        if (look)
        {
            _idleTicks = 0;
            foreach (var message in await _remote.OutboxAsync(cancellationToken))
                await DeliverAsync(message, cancellationToken);
        }

        foreach (var message in _sessionStore.UnansweredFrom(_remote.Name!))
        {
            if (message.RunId is { } runId && RunAnswer.Of(_hub, message.To, runId) is { } answer)
            {
                await _remote.AnsweredAsync(message.MessageId, answer, cancellationToken);
                _sessionStore.SetMessageAnswer(message.MessageId, answer.State, answer.Reply, answer.Error);
            }
        }
    }

    // While no helper there is at work: look every 5 seconds.
    private const int IdleTicks = 5;

    private async Task DeliverAsync(HelperMessage message, CancellationToken cancellationToken)
    {
        var (messageId, from, to, text) = (message.MessageId, message.From, message.To, message.Text);
        // Seen before: delivered (or not) before the remote heard. Tell it again; never deliver twice.
        if (_sessionStore.Message(messageId) is { } recorded)
        {
            await _remote.DeliveredAsync(messageId, recorded.RunId,
                recorded.Error ?? "The Mac stopped before it could deliver the message.", cancellationToken);
            return;
        }

        _sessionStore.AddMessage(from, to, text, messageId);
        string? runId = null;
        string? error = null;
        if (_sessionStore.Get(to) is { Machine: null } target)
        {
            var sender = _sessionStore.Get(from) ?? new SessionInfo(from, "", target.RepoUrl, null, null, 0);
            runId = await _local.SendAsync(target, MessageFrame.Of(messageId, sender, text,
                DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt)), cancellationToken);
            error = runId is null ? "That session is busy and can't take a message now; try again when it's done." : null;
        }
        else
            error = "There's no such session on the Mac.";

        if (runId is not null)
            _sessionStore.SetMessageRun(messageId, runId);
        else
        {
            _sessionStore.SetMessageFailed(messageId, error!);
            _sessionStore.SetMessageAnswer(messageId, "failed", "", error);
        }
        await _remote.DeliveredAsync(messageId, runId, error, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _polling;
        _stopping.Dispose();
    }
}
