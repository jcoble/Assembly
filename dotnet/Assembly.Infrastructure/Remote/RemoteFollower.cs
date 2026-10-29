using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Domain.Rows;

namespace Assembly.Infrastructure.Remote;

/// <summary>Copies remote rows through the machine socket. Each session copies catch-up before consuming live pushes.</summary>
internal sealed class RemoteFollower(
    LiveHub hub, Func<string, long, CancellationToken, IAsyncEnumerable<JsonElement>> subscribe,
    Func<string, long, CancellationToken, Task<IReadOnlyList<StoredRow>>> read,
    Func<CancellationToken, Task> checkFormat, Func<string, Task> turnEnded,
    Action<string, StoredRow> subagentCopied, TextWriter log) : IAsyncDisposable
{
    // The sessions followed now; those watched again while followed, owed one more pass; and those given up on because
    // the remote was unreachable, until it answers again. All three under _following's lock.
    private readonly Dictionary<string, Task> _following = [];
    private readonly HashSet<string> _again = [];
    private readonly HashSet<string> _gaveUp = [];
    // Remote helpers a restart found shown running here, until their follower first catches up. Under the same lock.
    private readonly HashSet<string> _shownRunning = [];
    private readonly CancellationTokenSource _stopping = new();
    internal string[] FollowedSessions
    {
        get { lock (_following) return [.. _following.Keys.Concat(_gaveUp).Distinct()]; }
    }

    /// <summary>
    /// Catches the session up, and follows it for as long as it's needed. A session already followed gets one more
    /// pass once its follower would stop, so a watch that lands as it stops isn't lost.
    /// </summary>
    public void Watch(string sessionId)
    {
        lock (_following)
        {
            if (_stopping.IsCancellationRequested)
                return;
            if (_following.ContainsKey(sessionId))
                _again.Add(sessionId);
            else
                _following[sessionId] = Task.Run(() => FollowAsync(sessionId));
        }
    }

    /// <summary>The remote answered again: follows the sessions given up on while it was unreachable.</summary>
    public void Resume()
    {
        string[] gaveUp;
        lock (_following)
        {
            gaveUp = [.. _gaveUp];
            _gaveUp.Clear();
        }
        foreach (var sessionId in gaveUp)
            Watch(sessionId);
    }

    /// <summary>Helpers a restart found shown running: each is ended once its follower catches up with no turn open.</summary>
    public void ShownRunning(IEnumerable<string> helperIds)
    {
        lock (_following)
            _shownRunning.UnionWith(helperIds);
    }

    public bool TakeShownRunning(string sessionId)
    {
        lock (_following)
            return _shownRunning.Remove(sessionId);
    }

    private bool Again(string sessionId)
    {
        lock (_following)
            return _again.Remove(sessionId);
    }

    private bool Needed(string sessionId) => hub.OpenRun(sessionId) is not null || hub.HasSubscribers(sessionId);

    private async Task FollowAsync(string sessionId)
    {
        var gaveUp = false;
        var again = false;
        try
        {
            do
            {
                await checkFormat(_stopping.Token);
                await FollowOnceAsync(sessionId, _stopping.Token);
            } while (!_stopping.IsCancellationRequested && Again(sessionId));
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception error) when (error is MachineUnreachable or CallFailed or IOException or System.Net.WebSockets.WebSocketException)
        {
            log.WriteLine($"remote session {sessionId}: {error.Message}");
            gaveUp = true;
        }
        finally
        {
            lock (_following)
            {
                _following.Remove(sessionId);
                again = _again.Remove(sessionId);
                if (gaveUp && !again) _gaveUp.Add(sessionId);
            }
        }
        if (!_stopping.IsCancellationRequested && (again || (!gaveUp && Needed(sessionId)))) Watch(sessionId);
    }

    private async Task FollowOnceAsync(string sessionId, CancellationToken stopping)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var caughtUp = false;
        void Unwatched(string session)
        {
            if (session != sessionId || !caughtUp || Needed(sessionId)) return;
            // The event can still reach this follow after it finished and disposed its source.
            try { done.Cancel(); } catch (ObjectDisposedException) { }
        }
        hub.Unwatched += Unwatched;
        try
        {
            await foreach (var frame in subscribe(sessionId, hub.LastSeq(sessionId), done.Token))
            {
                if (frame.GetProperty("t").GetString() == "state")
                {
                    // A bounded raw read also marks the end of catch-up, so an idle follower needs no quiet timer.
                    // The subscription's queued pushes are consumed afterwards; already copied seqs are skipped.
                    caughtUp = false;
                    var target = frame.GetProperty("seq").GetInt64();
                    IReadOnlyList<StoredRow> rows;
                    var interrupted = false;
                    do
                    {
                        try { rows = await read(sessionId, hub.LastSeq(sessionId), done.Token); }
                        catch (Exception error) when (error is IOException or System.Net.WebSockets.WebSocketException)
                        {
                            // The machine reconnects and sends a state from the last copied seq.
                            interrupted = true;
                            break;
                        }
                        foreach (var row in rows) await CopyAsync(row);
                        if (rows.Count == 0 && hub.LastSeq(sessionId) < target)
                            throw new IOException("remote catch-up made no progress");
                    } while (rows.Count == RowsFeature.PageLimit || hub.LastSeq(sessionId) < target);
                    if (interrupted) continue;
                    caughtUp = true;
                    if (TakeShownRunning(sessionId) && hub.OpenRun(sessionId) is null && hub.LastTurnEnd(sessionId) > 0)
                        await turnEnded(sessionId);
                }
                else if (caughtUp && frame.GetProperty("t").GetString() == "rows")
                    await CopyAsync(RowWire.Read(frame.GetProperty("row")));
                if (caughtUp && !Needed(sessionId)) return;
            }
        }
        catch (OperationCanceledException) when (done.IsCancellationRequested && !stopping.IsCancellationRequested) { }
        finally { hub.Unwatched -= Unwatched; }
    }

    private async Task CopyAsync(StoredRow row)
    {
        if (row.Seq <= hub.LastSeq(row.SessionId)) return;
        if (row.Kind == RowKind.Subagent) subagentCopied(row.SessionId, row);
        if (!hub.PublishCopy(row)) throw new IOException($"seq {row.Seq} doesn't follow {hub.LastSeq(row.SessionId)}");
        if (row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started")
        {
            TakeShownRunning(row.SessionId);
            await turnEnded(row.SessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] following;
        lock (_following)
        {
            _stopping.Cancel();
            following = [.. _following.Values];
        }
        await Task.WhenAll(following);
        _stopping.Dispose();
    }
}
