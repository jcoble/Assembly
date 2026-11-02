using System.Text.Json;
using System.Net.WebSockets;
using System.Threading.Channels;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Mirror;
using Assembly.Engine.Reading;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

/// <summary>One remote's read-only replica. Its existing socket owns reconnect; this owner has no timer.</summary>
internal sealed class EngineMirror : IAsyncDisposable
{
    private readonly RemoteMachine _remote;
    private readonly IAtomicWriter _writer;
    private readonly TimeProvider _time;
    private readonly TextWriter _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<CancellationToken> _opens = Channel.CreateUnbounded<CancellationToken>(new() { SingleReader = true });
    private readonly Lock _gate = new();
    private CancellationTokenSource? _connection;
    private readonly Task _run;
    public event Action? CaughtUp;

    public EngineMirror(RemoteMachine remote, IAtomicWriter writer, TimeProvider time, TextWriter log)
    {
        (_remote, _writer, _time, _log) = (remote, writer, time, log);
        remote.SocketOpened += Opened;
        _run = RunAsync();
    }

    private void Opened()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            _connection?.Cancel();
            _connection?.Dispose();
            _connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _opens.Writer.TryWrite(_connection.Token);
        }
    }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();
    private async Task RunAsync()
    {
        try
        {
            await foreach (var ct in _opens.Reader.ReadAllAsync(_stop.Token))
            {
                try { ct.ThrowIfCancellationRequested(); await FollowAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception error) when (error is MachineUnreachable or CallFailed or IOException or WebSocketException or ObjectDisposedException)
                {
                    _log.WriteLine($"engine mirror {_remote.Name}: {error.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        var info = await _remote.CallAsync("engine", "machine", JsonSerializer.Deserialize("{}", EngineJson.Default.JsonElement), false, ct);
        var machine = info.GetProperty("machine").GetString()!;
        var watermark = info.GetProperty("through").GetInt64();
        var cursor = _writer.Write("engine.mirror.open", s => MirrorApply.Open(s, machine, _remote.Name!, Now));
        if (cursor.State == "snapshotting") cursor = _writer.Write("engine.mirror.begin", s => MirrorApply.BeginSnapshot(s, machine, Now));
        var caughtUp = false;
        while (!ct.IsCancellationRequested)
        {
            long? cut = null;
            CursorAt? cutAt = null;
            await foreach (var frame in _remote.EngineFramesAsync(() => cursor.State == "live" ? (cursor.Through, cursor.At) : (null, null), ct))
            {
                var type = frame.GetProperty("t").GetString();
                if (type == "error")
                {
                    if (frame.GetProperty("code").GetString() == "bad_args" && frame.GetProperty("message").GetString() == "Invalid engine cursor")
                    {
                        cursor = _writer.Write("engine.mirror.begin", s => MirrorApply.BeginSnapshot(s, machine, Now));
                        break;
                    }
                    throw new CallFailed(frame.GetProperty("message").GetString()!);
                }
                if (frame.GetProperty("machine").GetString() != machine) throw new IOException("Foreign engine mirror frame");
                var through = frame.GetProperty("through").GetInt64();
                var at = frame.TryGetProperty("at", out var fingerprint) ? JsonSerializer.Deserialize(fingerprint, EngineJson.Default.CursorAt) : null;
                var more = frame.GetProperty("more").GetBoolean();
                var id = frame.GetProperty("id").GetInt64();
                if (type == "engine_snapshot")
                {
                    if (cut is null)
                    {
                        cursor = _writer.Write("engine.mirror.begin", s => MirrorApply.BeginSnapshot(s, machine, Now));
                        cut = through; cutAt = at;
                    }
                    if (cut != through || cutAt != at) throw new IOException("Engine snapshot cut differs between pages");
                    cursor = _writer.Write("engine.mirror.snapshot", s =>
                    {
                        MirrorApply.SnapshotPage(s, machine, frame.GetProperty("items").EnumerateArray().ToArray());
                        return more ? cursor : MirrorApply.EndSnapshot(s, machine, through, at, Now);
                    });
                    if (more) { await _remote.SendAsync($"{{\"t\":\"next\",\"id\":{id}}}", ct); continue; }
                    cut = null; cutAt = null;
                }
                else
                {
                    try
                    {
                        cursor = _writer.Write("engine.mirror.events", s => MirrorApply.Events(s, machine,
                            frame.GetProperty("after").GetInt64(), through, at,
                            JsonSerializer.Deserialize(frame.GetProperty("events"), EngineJson.Default.IReadOnlyListEngineEvent)!,
                            frame.GetProperty("rows").EnumerateArray().ToArray(), Now));
                    }
                    catch (MirrorGap) { break; }
                }
                // Write returned after AtomicWriter committed and published. Only now release the remote page.
                await _remote.SendAsync($"{{\"t\":\"ack\",\"id\":{id},\"stream\":\"engine\",\"after\":{through}}}", ct);
                if (!caughtUp && (type == "engine_snapshot" || through >= watermark)) { caughtUp = true; CaughtUp?.Invoke(); }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _remote.SocketOpened -= Opened;
        await _stop.CancelAsync();
        try { await _run; }
        finally { lock (_gate) { _connection?.Dispose(); } _stop.Dispose(); }
    }
}
