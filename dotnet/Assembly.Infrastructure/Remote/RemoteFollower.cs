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
    Action<string, StoredRow> subagentCopied, TextWriter log, Action<string, long>? reportCaughtUp = null,
    Action<string, string>? turnRead = null) : IAsyncDisposable
{
    // The sessions followed now; those watched again while followed, owed one more pass; and those given up on because
    // the remote was unreachable, until it answers again. All three under _following's lock.
    private readonly Dictionary<string, Task> _following = [];
    // Each followed session's own stop, so one session can be forgotten while the others carry on.
    private readonly Dictionary<string, CancellationTokenSource> _stops = [];
    private readonly HashSet<string> _again = [];
    private readonly HashSet<string> _gaveUp = [];
    // Remote helpers a restart found shown running here, until their follower first catches up. Under the same lock.
    private readonly HashSet<string> _shownRunning = [];
    private readonly Dictionary<string, string> _expectedRuns = [];
    private readonly CancellationTokenSource _stopping = new();
    internal string[] FollowedSessions
    {
        get { lock (_following) return [.. _following.Keys.Concat(_gaveUp).Distinct()]; }
    }

    /// <summary>
    /// Catches the session up, and follows it for as long as it's needed. A session already followed gets one more
    /// pass once its follower would stop, so a watch that lands as it stops isn't lost.
    /// </summary>
    public void Watch(string sessionId, string? expectedRun = null)
    {
        lock (_following)
        {
            if (_stopping.IsCancellationRequested)
                return;
            if (expectedRun is not null) _expectedRuns[sessionId] = expectedRun;
            if (_following.ContainsKey(sessionId))
                _again.Add(sessionId);
            else
            {
                var stop = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                _stops[sessionId] = stop;
                _following[sessionId] = Task.Run(() => FollowAsync(sessionId, stop.Token));
            }
        }
    }

    /// <summary>
    /// Stops following the session and waits until its follow has ended, before its saved copy is dropped (TSK-1692).
    /// The next follow starts from the rows saved then.
    /// </summary>
    public async Task ForgetAsync(string sessionId)
    {
        Task? running;
        lock (_following)
        {
            _again.Remove(sessionId);
            _gaveUp.Remove(sessionId);
            _expectedRuns.Remove(sessionId);
            running = _following.GetValueOrDefault(sessionId);
            _stops.GetValueOrDefault(sessionId)?.Cancel();
        }
        if (running is not null) await running;
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

    private bool Needed(string sessionId)
    {
        string? expected;
        lock (_following) expected = _expectedRuns.GetValueOrDefault(sessionId);
        // A user row precedes started. Keep following until the run's turn row is copied,
        // including when another follow copied it before the send response arrived.
        if (expected is not null && hub.RunRows(sessionId, expected).Any(row => row.Kind == RowKind.Turn))
        {
            lock (_following)
                if (_expectedRuns.GetValueOrDefault(sessionId) == expected) _expectedRuns.Remove(sessionId);
            expected = null;
        }
        return expected is not null || hub.OpenRun(sessionId) is not null || hub.HasSubscribers(sessionId);
    }

    // stop: this session's own, linked to the whole follower's.
    private async Task FollowAsync(string sessionId, CancellationToken stop)
    {
        var gaveUp = false;
        var again = false;
        try
        {
            do
            {
                await checkFormat(stop);
                await FollowOnceAsync(sessionId, stop);
            } while (!stop.IsCancellationRequested && Again(sessionId));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is MachineUnreachable or CallFailed or IOException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
        {
            log.WriteLine($"remote session {sessionId}: {error.Message}");
            gaveUp = true;
        }
        finally
        {
            lock (_following)
            {
                _following.Remove(sessionId);
                if (_stops.Remove(sessionId, out var own)) own.Dispose();
                if (gaveUp) _expectedRuns.Remove(sessionId);
                again = _again.Remove(sessionId);
                if (gaveUp && !again) _gaveUp.Add(sessionId);
            }
        }
        // A forgotten session is followed again only when something watches it anew.
        if (!stop.IsCancellationRequested && (again || (!gaveUp && Needed(sessionId)))) Watch(sessionId);
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
                    // For a saved copy, a state at its last seq means the subscription's own frames carry the rest,
                    // so nothing is read (TSK-1692: reopening a saved session asks the remote for no rows).
                    caughtUp = false;
                    var target = frame.GetProperty("seq").GetInt64();
                    var interrupted = false;
                    if (target > hub.LastSeq(sessionId) || hub.Copies.RemoteCopyOf(sessionId) is null)
                    {
                        IReadOnlyList<StoredRow> rows;
                        do
                        {
                            try { rows = await read(sessionId, hub.LastSeq(sessionId), done.Token); }
                            catch (Exception error) when (error is IOException or System.Net.WebSockets.WebSocketException or ObjectDisposedException ||
                                error is OperationCanceledException && !done.IsCancellationRequested)
                            {
                                // The machine reconnects and sends a state from the last copied seq.
                                interrupted = true;
                                break;
                            }
                            foreach (var row in rows) await CopyAsync(row);
                            if (rows.Count == 0 && hub.LastSeq(sessionId) < target)
                                throw new IOException("remote catch-up made no progress");
                        } while (rows.Count > 0);
                    }
                    if (interrupted) continue;
                    caughtUp = true;
                    // A saved copy waiting for the follower to reach the remote's head can now be checked (TSK-1692).
                    reportCaughtUp?.Invoke(sessionId, hub.LastSeq(sessionId));
                    if (TakeShownRunning(sessionId) && hub.OpenRun(sessionId) is null && hub.LastTurnEnd(sessionId) > 0)
                        await turnEnded(sessionId);
                }
                else if (caughtUp && frame.GetProperty("t").GetString() == "rows")
                {
                    await CopyAsync(RowWire.Read(frame.GetProperty("row")));
                    // The catch-up the subscription carried, or a live row, can be what a waiting copy needs.
                    reportCaughtUp?.Invoke(sessionId, hub.LastSeq(sessionId));
                }
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
            // The run's user rows may have changed on the remote since they were copied (TSK-1692).
            if (row.RunId is { } run) turnRead?.Invoke(row.SessionId, run);
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
