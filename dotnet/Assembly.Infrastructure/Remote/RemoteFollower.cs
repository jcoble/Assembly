using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Domain.Rows;
using Assembly.Application.Features.Chat;

namespace Assembly.Infrastructure.Remote;

/// <summary>
/// Copies a remote session's rows into the desktop's store, in seq order, and publishes them on the desktop's hub. One socket
/// per session to the remote's <c>/live?after=</c>, opened from the desktop's last seq for the session: the backlog is the
/// catch-up, then live rows follow. It keeps following while the turn runs or a webview has the session open, and
/// reconnects from the last seq when the link drops or a row doesn't follow the one before it.
/// </summary>
internal sealed class RemoteFollower(
    Uri address, string token, LiveHub hub, Func<CancellationToken, Task> checkFormat, Action linked, Func<string, Task> turnEnded,
    Action<string, StoredRow> subagentCopied, TextWriter log) : IAsyncDisposable
{
    // How long a socket may stay quiet, with nobody watching and no turn running, before it counts as caught up.
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    // The sessions followed now; those watched again while followed, owed one more pass; and those given up on because
    // the remote was unreachable, until it answers again. All three under _following's lock.
    private readonly Dictionary<string, Task> _following = [];
    private readonly HashSet<string> _again = [];
    private readonly HashSet<string> _gaveUp = [];
    // Remote helpers a restart found shown running here, until their follower first catches up. Under the same lock.
    private readonly HashSet<string> _shownRunning = [];
    private readonly CancellationTokenSource _stopping = new();
    private int _links;

    /// <summary>Whether any session's socket to the remote is open now, which shows the remote is there.</summary>
    public bool Linked => Volatile.Read(ref _links) > 0;

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

    private bool IsShownRunning(string sessionId)
    {
        lock (_following)
            return _shownRunning.Contains(sessionId);
    }

    private bool Again(string sessionId)
    {
        lock (_following)
            return _again.Remove(sessionId);
    }

    private bool Needed(string sessionId) => hub.OpenRun(sessionId) is not null || hub.HasSubscribers(sessionId);

    private async Task FollowAsync(string sessionId)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var gaveUp = false;
        var again = false;
        try
        {
            // At least once, so opening a session catches up on turns that ran while nobody was watching.
            do
            {
                try
                {
                    await checkFormat(_stopping.Token);
                    await FollowOnceAsync(sessionId, _stopping.Token);
                    backoff = TimeSpan.FromSeconds(1);
                }
                catch (Exception error) when (error is MachineUnreachable or CallFailed)
                {
                    log.WriteLine($"remote session {sessionId}: {error.Message}");
                    gaveUp = true;
                    break;
                }
                catch (Exception error) when (error is WebSocketException or HttpRequestException or IOException
                                              || (error is OperationCanceledException && !_stopping.IsCancellationRequested))
                {
                    log.WriteLine($"remote session {sessionId}: link lost ({error.GetType().Name}); again in {backoff.TotalSeconds:0} s");
                    await Task.Delay(backoff, _stopping.Token);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                }
            }
            // A helper a restart found shown running is followed until a real catch-up decides its row; not in Needed,
            // which would keep it from ever settling.
            while (!_stopping.IsCancellationRequested && (Again(sessionId) | Needed(sessionId) || IsShownRunning(sessionId)));
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_following)
            {
                _following.Remove(sessionId);
                again = _again.Remove(sessionId);
                // A watch that came meanwhile wins over giving up.
                if (gaveUp && !again)
                    _gaveUp.Add(sessionId);
            }
        }

        // Watched or needed again between the last check and the removal: start over. A give-up doesn't start over on
        // its own; Resume or a new watch does.
        if (!_stopping.IsCancellationRequested && (again || (!gaveUp && Needed(sessionId))))
            Watch(sessionId);
    }

    // One socket, until it's no longer needed, drops, or a row arrives out of order.
    private async Task FollowOnceAsync(string sessionId, CancellationToken stopping)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        var live = new UriBuilder(address) { Scheme = address.Scheme == "https" ? "wss" : "ws", Path = "/live", Query = $"session={Uri.EscapeDataString(sessionId)}&after={hub.LastSeq(sessionId)}" };
        await socket.ConnectAsync(live.Uri, stopping);
        Interlocked.Increment(ref _links);
        // The format was checked before the connect, so an open link shows the remote is there.
        linked();

        using var done = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var lastFrame = DateTime.UtcNow;
        var settling = SettleAsync(sessionId, () => lastFrame, done);
        try
        {
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                buffer.SetLength(0);
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(chunk, done.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                        return;
                    buffer.Write(chunk, 0, received.Count);
                }
                while (!received.EndOfMessage);

                lastFrame = DateTime.UtcNow;
                using var frame = JsonDocument.Parse(buffer.ToArray());
                if (frame.RootElement.GetProperty("type").GetString() != "row")
                    continue; // a ping
                var row = RowWire.Read(frame.RootElement.GetProperty("row"));
                // A subagent's session is known before its row is shown, so it can be opened.
                if (row.Kind == RowKind.Subagent)
                    subagentCopied(sessionId, row);
                if (!hub.PublishCopy(row))
                {
                    log.WriteLine($"remote session {sessionId}: seq {row.Seq} doesn't follow {hub.LastSeq(sessionId)}; catching up again");
                    return;
                }
                if (row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started")
                {
                    TakeShownRunning(sessionId);
                    await turnEnded(sessionId);
                }
            }
        }
        catch (OperationCanceledException) when (done.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            // Caught up, and nobody needs more. Only this exit, a real read that found nothing newer, ends a helper's row
            // a restart found shown running; a give-up, a dropped link, a stop or a seq mismatch leaves it running.
            if (TakeShownRunning(sessionId) && hub.OpenRun(sessionId) is null && hub.LastTurnEnd(sessionId) > 0)
                await turnEnded(sessionId);
        }
        finally
        {
            Interlocked.Decrement(ref _links);
            await done.CancelAsync();
            await settling;
        }
    }

    // Ends the socket once it has been quiet for a while with nobody watching and no turn running.
    private async Task SettleAsync(string sessionId, Func<DateTime> lastFrame, CancellationTokenSource done)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), done.Token);
                if (DateTime.UtcNow - lastFrame() >= Settled && !Needed(sessionId))
                {
                    await done.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
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
