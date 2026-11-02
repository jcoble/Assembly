using Assembly.Application.Features.Chat.Sessions.Configuration;
using System.Threading.Channels;
using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;

using Assembly.Application.Common.Agents;
namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// Splits one adapter's <c>session/update</c> stream by session, since every session's ACP session shares the adapter.
/// Updates for a session nobody has asked for yet wait in its own queue. Updates for a session that was closed and
/// not resumed are dropped, so they never reach its next turn. A session resumed on the same ACP id is open again from
/// the client's synchronous resume-response callback, so what the agent sent before its answer is still dropped and what it sent after is kept. <see cref="Flush"/> moves what has arrived so far,
/// so a turn's end can take every update the agent sent before its answer. Provider Task tools are display updates under their existing session.
/// </summary>
internal sealed class UpdateRouter
{
    private readonly ChannelReader<SessionNotification> _all;
    private readonly AcpClient? _client;
    private readonly Dictionary<string, Channel<SessionNotification>> _acpSessions = [];
    private readonly HashSet<string> _closedAcpSessions = [];
    private readonly Lock _gate = new();
    private bool _closed;

    public UpdateRouter(AcpClient client) : this(client.Updates, client) { }
    public UpdateRouter(ChannelReader<SessionNotification> all) : this(all, null) { }

    public void Resume(string sessionId)
    {
        lock (_gate)
        {
            Flush();
            _closedAcpSessions.Remove(sessionId);
        }
    }

    private UpdateRouter(ChannelReader<SessionNotification> all, AcpClient? client)
    {
        _all = all;
        _client = client;
        if (client is not null) client.SessionResumed += Resume;
        Completion = PumpAsync();
    }

    /// <summary>Completes when the adapter's updates have ended and every session's queue with them.</summary>
    public Task Completion { get; }

    /// <summary>The session's updates, in arrival order. Completes when the session is forgotten or the adapter ends.</summary>
    public ChannelReader<SessionNotification> For(string acpSessionId)
    {
        lock (_gate)
            return Queue(acpSessionId).Reader;
    }

    // Metadata consumers run before the normal update pump for this scope. Keep all replay/text/tool updates in order.
    internal ConfigObservation Metadata(string nativeId, JsonElement responseOptions, string? responseCommands)
    {
        lock (_gate)
        {
            Flush();
            var retained = new List<SessionNotification>();
            var options = responseOptions;
            var commands = responseCommands;
            var queue = Queue(nativeId);
            while (queue.Reader.TryRead(out var update))
            {
                if (update.Kind == "available_commands_update" && update.Update.TryGetProperty("availableCommands", out var available))
                {
                    if (responseCommands is null) commands = available.GetRawText();
                }
                else if (update.Kind == "config_option_update" && update.Update.TryGetProperty("configOptions", out var changed))
                {
                    // A complete ACP2 response follows the preceding notifications and is authoritative.
                    if (responseOptions.ValueKind != JsonValueKind.Array) options = changed.Clone();
                }
                else retained.Add(update);
            }
            if (_closed) _acpSessions[nativeId] = queue = Channel.CreateUnbounded<SessionNotification>();
            foreach (var update in retained) queue.Writer.TryWrite(update);
            if (_closed) queue.Writer.TryComplete();
            return new ConfigObservation(options, commands);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            while (_all.TryRead(out var update))
            {
                var target = update.SessionId;
                if (_closedAcpSessions.Contains(target))
                    Console.Error.WriteLine($"session {update.SessionId}: dropped a {update.Kind} that came after it was closed");
                else
                    Queue(target).Writer.TryWrite(update);
            }
        }
    }

    /// <summary>The session is closed: its queue completes, and goes. What arrived before it is read first, so a resume
    /// response callback that came before the close can't open the session again after it.</summary>
    public void Forget(string acpSessionId)
    {
        lock (_gate)
        {
            Flush();
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

        if (_client is not null) _client.SessionResumed -= Resume;
        lock (_gate)
        {
            _closed = true;
            foreach (var queue in _acpSessions.Values)
                queue.Writer.TryComplete();
        }
    }
}
