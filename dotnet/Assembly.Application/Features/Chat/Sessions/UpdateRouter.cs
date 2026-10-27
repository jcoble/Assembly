using System.Threading.Channels;
using Assembly.Application.Common.Acp.Messages;

using Assembly.Application.Common.Agents;
namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// Splits one adapter's <c>session/update</c> stream by session, since every session's ACP session shares the adapter.
/// Updates for a session nobody has asked for yet wait in its own queue. Updates for a session that was closed and
/// not reopened are dropped, so they never reach its next turn. <see cref="Flush"/> moves what has arrived so far,
/// so a turn's end can take every update the agent sent before its answer. A subagent's child session, once its
/// parent announces it (<c>subagent_update</c>), is read with its parent: its updates go to the parent's queue.
/// </summary>
internal sealed class UpdateRouter
{
    private readonly ChannelReader<SessionNotification> _all;
    private readonly Dictionary<string, Channel<SessionNotification>> _acpSessions = [];
    private readonly HashSet<string> _closedAcpSessions = [];
    // Each announced child session's root session: the queue its updates go to.
    private readonly Dictionary<string, string> _parents = [];
    private readonly Lock _gate = new();
    private bool _closed;

    public UpdateRouter(ChannelReader<SessionNotification> all)
    {
        _all = all;
        _ = PumpAsync();
    }

    /// <summary>The session's updates, in arrival order. Completes when the session is forgotten or the adapter ends.</summary>
    public ChannelReader<SessionNotification> For(string acpSessionId)
    {
        lock (_gate)
            return Queue(acpSessionId).Reader;
    }

    public void Flush()
    {
        lock (_gate)
        {
            while (_all.TryRead(out var update))
            {
                var target = _parents.GetValueOrDefault(update.SessionId, update.SessionId);
                if (update.Kind == "subagent_update" && update.Update.TryGetProperty("sessionId", out var child)
                    && child.GetString() is { Length: > 0 } childId && childId != target)
                    _parents[childId] = target;
                if (_closedAcpSessions.Contains(target))
                    Console.Error.WriteLine($"session {update.SessionId}: dropped a {update.Kind} that came after it was closed");
                else
                    Queue(target).Writer.TryWrite(update);
            }
        }
    }

    /// <summary>A closed session is being reopened: its updates are wanted again.</summary>
    public void Reopen(string acpSessionId)
    {
        Flush();
        lock (_gate)
            _closedAcpSessions.Remove(acpSessionId);
    }

    /// <summary>The session is closed: its queue completes, and goes.</summary>
    public void Forget(string acpSessionId)
    {
        lock (_gate)
        {
            _closedAcpSessions.Add(acpSessionId);
            if (_acpSessions.Remove(acpSessionId, out var queue))
                queue.Writer.TryComplete();
        }
    }

    // Called with the lock held.
    private Channel<SessionNotification> Queue(string acpSessionId)
    {
        if (!_acpSessions.TryGetValue(acpSessionId, out var queue))
        {
            queue = _acpSessions[acpSessionId] = Channel.CreateUnbounded<SessionNotification>();
            if (_closed)
                queue.Writer.TryComplete();
        }

        return queue;
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _all.WaitToReadAsync())
                Flush();
        }
        catch (Exception)
        {
            // The adapter is gone; its sessions' queues end below.
        }

        lock (_gate)
        {
            _closed = true;
            foreach (var queue in _acpSessions.Values)
                queue.Writer.TryComplete();
        }
    }
}
