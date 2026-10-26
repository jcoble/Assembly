using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Domain.Rows;
using Assembly.Application.Features.Chat;

namespace Assembly.Infrastructure.Remote;

/// <summary>
/// Copies a remote thread's rows into the Mac's store, in seq order, and publishes them on the Mac's hub. One socket
/// per thread to the remote's <c>/live?after=</c>, opened from the Mac's last seq for the thread: the backlog is the
/// catch-up, then live rows follow. It keeps following while the turn runs or a webview has the thread open, and
/// reconnects from the last seq when the link drops or a row doesn't follow the one before it.
/// </summary>
internal sealed class RemoteFollower(
    Uri address, string token, LiveHub hub, Func<CancellationToken, Task> checkFormat, Func<string, Task> turnEnded,
    Action<string, StoredRow> subagentCopied, TextWriter log) : IAsyncDisposable
{
    // How long a socket may stay quiet, with nobody watching and no turn running, before it counts as caught up.
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Task> _following = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Catches the thread up, and follows it for as long as it's needed. A thread already followed is left as it is.</summary>
    public void Watch(string threadId)
    {
        if (!_stopping.IsCancellationRequested)
            _following.GetOrAdd(threadId, id => Task.Run(() => FollowAsync(id)));
    }

    private bool Needed(string threadId) => hub.OpenRun(threadId) is not null || hub.HasSubscribers(threadId);

    private async Task FollowAsync(string threadId)
    {
        var backoff = TimeSpan.FromSeconds(1);
        try
        {
            // At least once, so opening a thread catches up on turns that ran while nobody was watching.
            do
            {
                try
                {
                    await checkFormat(_stopping.Token);
                    await FollowOnceAsync(threadId, _stopping.Token);
                    backoff = TimeSpan.FromSeconds(1);
                }
                catch (MachineUnreachable error)
                {
                    log.WriteLine($"remote thread {threadId}: {error.Message}");
                    return;
                }
                catch (Exception error) when (error is WebSocketException or HttpRequestException or IOException
                                              || (error is OperationCanceledException && !_stopping.IsCancellationRequested))
                {
                    log.WriteLine($"remote thread {threadId}: link lost ({error.GetType().Name}); again in {backoff.TotalSeconds:0} s");
                    await Task.Delay(backoff, _stopping.Token);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                }
            }
            while (!_stopping.IsCancellationRequested && Needed(threadId));
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        finally
        {
            _following.TryRemove(threadId, out _);
        }

        // Needed again between the last check and the removal: start over.
        if (!_stopping.IsCancellationRequested && Needed(threadId))
            Watch(threadId);
    }

    // One socket, until it's no longer needed, drops, or a row arrives out of order.
    private async Task FollowOnceAsync(string threadId, CancellationToken stopping)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        var live = new UriBuilder(address) { Scheme = address.Scheme == "https" ? "wss" : "ws", Path = "/live", Query = $"thread={Uri.EscapeDataString(threadId)}&after={hub.LastSeq(threadId)}" };
        await socket.ConnectAsync(live.Uri, stopping);

        using var done = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var lastFrame = DateTime.UtcNow;
        var settling = SettleAsync(threadId, () => lastFrame, done);
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
                // A subagent's thread is known before its row is shown, so it can be opened.
                if (row.Kind == RowKind.Subagent)
                    subagentCopied(threadId, row);
                if (!hub.PublishCopy(row))
                {
                    log.WriteLine($"remote thread {threadId}: seq {row.Seq} doesn't follow {hub.LastSeq(threadId)}; catching up again");
                    return;
                }
                if (row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started")
                    await turnEnded(threadId);
            }
        }
        catch (OperationCanceledException) when (done.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            // Caught up, and nobody needs more.
        }
        finally
        {
            await done.CancelAsync();
            await settling;
        }
    }

    // Ends the socket once it has been quiet for a while with nobody watching and no turn running.
    private async Task SettleAsync(string threadId, Func<DateTime> lastFrame, CancellationTokenSource done)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), done.Token);
                if (DateTime.UtcNow - lastFrame() >= Settled && !Needed(threadId))
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
        await _stopping.CancelAsync();
        await Task.WhenAll(_following.Values);
        _stopping.Dispose();
    }
}
