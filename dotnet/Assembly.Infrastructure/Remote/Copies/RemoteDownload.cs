using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Rows.Copies;

namespace Assembly.Infrastructure.Remote.Copies;

/// <summary>
/// Downloads a remote session's history into its saved copy, newest first (TSK-1692): the first chunk is the newest
/// screen, then older chunks until the remote has none left. One loop per machine runs one session at a time. A download
/// cut by a lost connection carries on with the next one. Everything it saves goes through <see cref="LiveHub"/>.
/// </summary>
internal sealed class RemoteDownload : IAsyncDisposable
{
    private readonly LiveHub _hub;
    private readonly Func<string, JsonElement, CancellationToken, Task<JsonElement>> _bulkCall;
    private readonly Func<CancellationToken, Task<string>> _verifiedEngine;
    private readonly Action<string, StoredRow> _subagentCopied;
    private readonly TextWriter _log;
    private readonly Channel<string?> _work = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions { SingleReader = true });
    // Sessions whose download is queued or running, each with its first chunk's save; under its own lock.
    private readonly Dictionary<string, TaskCompletionSource> _downloading = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    /// <param name="bulkCall">A rows read on the machine's bulk socket: method, args, cancellation.</param>
    /// <param name="verifiedEngine">The remote's engine id, once its socket's identity is verified.</param>
    /// <param name="subagentCopied">Told each subagent card a chunk saved, so its child session is known here.</param>
    public RemoteDownload(LiveHub hub, Func<string, JsonElement, CancellationToken, Task<JsonElement>> bulkCall,
        Func<CancellationToken, Task<string>> verifiedEngine, Action<string, StoredRow> subagentCopied, TextWriter log)
    {
        _hub = hub;
        _bulkCall = bulkCall;
        _verifiedEngine = verifiedEngine;
        _subagentCopied = subagentCopied;
        _log = log;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>
    /// Starts the session's download, or joins the one running. Returns once its first chunk is saved, or at once when
    /// its copy already exists.
    /// </summary>
    public Task Ensure(string session, CancellationToken ct)
    {
        TaskCompletionSource first;
        lock (_downloading)
        {
            if (!_downloading.TryGetValue(session, out first!))
            {
                if (_hub.Copies.RemoteCopyOf(session) is not null) return Task.CompletedTask;
                _downloading[session] = first = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _work.Writer.TryWrite(session);
            }
        }
        return first.Task.WaitAsync(ct);
    }

    /// <summary>
    /// A machine socket opened: copies still downloading carry on. Called under the machine's send lock, so it only
    /// queues; the work runs on the download loop. A chunk call on the socket that closed has already failed with it.
    /// </summary>
    public void Connected() => _work.Writer.TryWrite(null);

    private async Task LoopAsync()
    {
        try
        {
            await foreach (var session in _work.Reader.ReadAllAsync(_stop.Token))
            {
                if (session is null) await ResumeAsync(_stop.Token);
                else await DownloadAsync(session, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    // Copies left downloading, by an earlier connection or an older install, carry on.
    private async Task ResumeAsync(CancellationToken ct)
    {
        string machine;
        try { machine = await _verifiedEngine(ct); }
        catch (Exception error) when (Failed(error, ct)) { _log.WriteLine($"remote downloads: {error.Message}"); return; }
        foreach (var copy in _hub.Copies.RemoteCopiesOn(machine).Where(copy => !copy.Complete))
            lock (_downloading)
                if (!_downloading.ContainsKey(copy.Session))
                {
                    _downloading[copy.Session] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _work.Writer.TryWrite(copy.Session);
                }
    }

    private async Task DownloadAsync(string session, CancellationToken ct)
    {
        TaskCompletionSource? first;
        lock (_downloading) first = _downloading.GetValueOrDefault(session);
        _hub.BeginDownload(session);
        try
        {
            if (await StartAsync(session, ct)) _hub.CompleteCopy(session);
            else
            {
                first?.TrySetResult();
                await BackfillAsync(session, ct);
                // The hole check (step 6) decides when a copy is whole; until it lands, the bottom reached is enough.
                _hub.CompleteCopy(session);
            }
            first?.TrySetResult();
        }
        catch (Exception error) when (Failed(error, ct))
        {
            _log.WriteLine($"remote session {session}: download stopped: {error.Message}");
            first?.TrySetException(error);
        }
        finally
        {
            _hub.EndDownload(session);
            lock (_downloading) _downloading.Remove(session);
        }
    }

    // The newest screen first. A forward copy the follower already holds is adopted instead. True when it's whole.
    private async Task<bool> StartAsync(string session, CancellationToken ct)
    {
        var machine = await _verifiedEngine(ct);
        if (_hub.Copies.RemoteCopyOf(session) is { } existing) return existing.Complete;
        if (_hub.LastSeq(session) > 0)
        {
            var heads = await _bulkCall("heads", JsonSerializer.SerializeToElement(new RowsFeature.HeadsArgs([session]), RowJsonContext.Rows.HeadsArgs), ct);
            var head = heads.Deserialize(RowJsonContext.Rows.HeadsReply)?.Sessions.GetValueOrDefault(session)?.Head ?? 0;
            _hub.AdoptCopy(session, machine, head);
            return false;
        }
        var chunk = await Before(session, null, RowsFeature.FirstChunkItems, ct);
        var start = _hub.StartCopy(session, machine, chunk);
        foreach (var row in start.SubagentRows) _subagentCopied(session, row);
        return start.Complete;
    }

    private async Task BackfillAsync(string session, CancellationToken ct)
    {
        while (_hub.Copies.FirstSeq(session) is { } oldest)
        {
            var chunk = await Before(session, oldest, null, ct);
            if (chunk.Rows.Count == 0) return;
            foreach (var row in _hub.Backfill(session, [.. chunk.Rows.Select(RowWire.Read)])) _subagentCopied(session, row);
        }
    }

    private async Task<BeforeChunk> Before(string session, long? before, int? items, CancellationToken ct)
    {
        var reply = await _bulkCall("before", JsonSerializer.SerializeToElement(new RowsFeature.BeforeArgs(session, before, items), RowJsonContext.Rows.BeforeArgs), ct);
        return reply.Deserialize(RowJsonContext.Rows.BeforeChunk) ?? throw new CallFailed("incompatible backend");
    }

    // A lost connection, an unreachable machine or a refused call stops this download; the loop carries on.
    private bool Failed(Exception error, CancellationToken ct) =>
        error is MachineUnreachable or CallFailed or IOException or System.Net.WebSockets.WebSocketException or ObjectDisposedException
            or JsonException or KeyNotFoundException or InvalidOperationException
        || error is OperationCanceledException && !_stop.IsCancellationRequested;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _work.Writer.TryComplete();
        await _loop;
        lock (_downloading)
            foreach (var first in _downloading.Values) first.TrySetCanceled();
        _stop.Dispose();
    }
}
