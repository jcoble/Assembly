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
    private readonly Action<IWriteScope, string>? _mirrored;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stoppingToken;
    internal CancellationToken Stopping => _stoppingToken;
    private readonly Channel<FollowConnection> _opens = Channel.CreateUnbounded<FollowConnection>(new() { SingleReader = true });
    private readonly Lock _gate = new();
    private FollowConnection? _connection;
    private readonly Task _run;
    private readonly List<Task> _cancellations = [];
    private LiveMirrorBinding? _live;
    private long _readinessLifetime;
    private bool _stopping;
    public event Action? CaughtUp;

    public EngineMirror(RemoteMachine remote, IAtomicWriter writer, TimeProvider time, TextWriter log, Action<IWriteScope, string>? mirrored = null)
    {
        (_remote, _writer, _time, _log) = (remote, writer, time, log);
        _mirrored = mirrored;
        _stoppingToken = _stop.Token;
        remote.SocketOpened += Opened;
        _run = RunAsync();
    }

    internal sealed class LiveMirrorBinding
    {
        private readonly EngineMirror _mirror;
        private readonly long _lifetime;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationToken _invalidated;
        private int _lost;
        internal string Machine { get; }
        internal long Generation { get; }
        internal RemoteMachine.OwnerConnectionBinding OwnerBinding { get; }
        internal bool IsInvalidated => Volatile.Read(ref _lost) != 0 || OwnerBinding.IsInvalidated;
        internal CancellationToken Invalidated => _invalidated;
        private LiveMirrorBinding(EngineMirror mirror, RemoteMachine.OwnerConnectionBinding owner, string machine, long generation, long lifetime)
        {
            (_mirror, OwnerBinding, Machine, Generation, _lifetime) = (mirror, owner, machine, generation, lifetime);
            _invalidated = _stop.Token;
        }
        internal static LiveMirrorBinding Publish(EngineMirror mirror, RemoteMachine.OwnerConnectionBinding owner,
            string machine, long generation, long lifetime) => new(mirror, owner, machine, generation, lifetime);
        internal bool Matches(EngineMirror mirror, long lifetime) => ReferenceEquals(_mirror, mirror) && _lifetime == lifetime && !IsInvalidated;
        internal void Revoke() => Interlocked.Exchange(ref _lost, 1);
        internal async Task CancelAsync()
        {
            try { await _stop.CancelAsync(); }
            finally { _stop.Dispose(); }
        }
    }

    // Called under _gate; atomic loss precedes asynchronous callback cleanup.
    private void LoseReadiness()
    {
        _readinessLifetime++;
        if (_live is not { } live) return;
        _live = null;
        live.Revoke();
        _cancellations.RemoveAll(task => task.IsCompletedSuccessfully);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellations.Add(CancelReadinessAfterAsync(start.Task, live));
        start.SetResult();
    }

    private static async Task CancelReadinessAfterAsync(Task start, LiveMirrorBinding binding)
    {
        await start;
        await binding.CancelAsync();
    }

    // Cancellation runs through this owned task, never inside the socket/mirror locks.
    private sealed class FollowConnection
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _cancel = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _cancellation;
        internal FollowConnection() => _cancellation = CancelAsync();
        internal CancellationToken Token => _stop.Token;
        internal void Revoke() => _cancel.TrySetResult();
        private async Task CancelAsync() { await _cancel.Task; await _stop.CancelAsync(); }
        internal async Task FinishAsync()
        {
            Revoke();
            try { await _cancellation; }
            finally { _stop.Dispose(); }
        }
    }

    private void Opened()
    {
        lock (_gate)
        {
            if (_stopping) return;
            LoseReadiness();
            _connection?.Revoke();
            _connection = new FollowConnection();
            _opens.Writer.TryWrite(_connection);
        }
    }

    internal async Task<LiveMirrorBinding?> CaptureLiveBindingAsync(CancellationToken ct)
    {
        LiveMirrorBinding? live;
        lock (_gate) live = _live;
        return live is not null && await IsCurrentLiveBindingAsync(live, ct) ? live : null;
    }

    internal async Task<bool> IsCurrentLiveBindingAsync(LiveMirrorBinding binding, CancellationToken ct)
    {
        lock (_gate)
            if (!ReferenceEquals(binding, _live) || !binding.Matches(this, _readinessLifetime)) return false;
        if (!await _remote.IsCurrentOwnerBindingAsync(binding.OwnerBinding, ct)) return false;
        lock (_gate) return ReferenceEquals(binding, _live) && binding.Matches(this, _readinessLifetime);
    }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();
    private async Task RunAsync()
    {
        try
        {
            await foreach (var connection in _opens.Reader.ReadAllAsync(_stop.Token))
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, connection.Token))
                {
                    var ct = linked.Token;
                    try { ct.ThrowIfCancellationRequested(); await FollowAsync(ct); }
                    catch (OperationCanceledException) { }
                    catch (Exception error) when (error is MachineUnreachable or CallFailed or IOException or WebSocketException or ObjectDisposedException)
                    {
                        _log.WriteLine($"engine mirror {_remote.Name}: {error.Message}");
                    }
                    finally
                    {
                        lock (_gate) { LoseReadiness(); if (ReferenceEquals(_connection, connection)) _connection = null; }
                        await connection.FinishAsync();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        var (info, owner) = await _remote.ReadMirrorOwnerAsync(ct);
        using var ownerStop = CancellationTokenSource.CreateLinkedTokenSource(ct, owner.Invalidated);
        ct = ownerStop.Token;
        var machine = info.Machine;
        var watermark = info.Through;
        var cursor = _writer.Write("engine.mirror.open", s =>
        {
            var current = MirrorApply.Open(s, machine, _remote.Name!, Now);
            // Pre-classification cached rows need a fresh owner snapshot even if no session has changed.
            var needsDisplay = s.QueryFirst("""
                SELECT COUNT(*) FROM engine_mirror_row WHERE machine=$machine AND generation=$generation
                  AND entity_kind='session' AND json_type(row_json,'$.isBare') IS NULL
                """, r => r.Long(0), new SqlArg("$machine", machine), new("$generation", current.Generation)) > 0;
            return current.State == "snapshotting" || needsDisplay ? MirrorApply.BeginSnapshot(s, machine, Now) : current;
        });
        var caughtUp = false;
        while (!ct.IsCancellationRequested)
        {
            long? cut = null;
            CursorAt? cutAt = null;
            await foreach (var frame in _remote.EngineFramesAsync(() => cursor.State == "live" ? (cursor.Through, cursor.At) : (null, null), ct, owner))
            {
                ct.ThrowIfCancellationRequested();
                if (!await _remote.IsCurrentOwnerBindingAsync(owner, ct)) throw new IOException("Mirror owner changed");
                var type = frame.GetProperty("t").GetString();
                if (type == "error")
                {
                    if (frame.GetProperty("code").GetString() == "bad_args" && frame.GetProperty("message").GetString() == "Invalid engine cursor")
                    {
                        lock (_gate) LoseReadiness();
                        caughtUp = false;
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
                        lock (_gate) LoseReadiness();
                        caughtUp = false;
                        cursor = _writer.Write("engine.mirror.begin", s => MirrorApply.BeginSnapshot(s, machine, Now));
                        cut = through; cutAt = at;
                    }
                    if (cut != through || cutAt != at) throw new IOException("Engine snapshot cut differs between pages");
                    cursor = _writer.Write("engine.mirror.snapshot", s =>
                    {
                        MirrorApply.SnapshotPage(s, machine, frame.GetProperty("items").EnumerateArray().ToArray());
                        if (more) return cursor;
                        var completed = MirrorApply.EndSnapshot(s, machine, through, at, Now);
                        _remote.ProjectSessionDisplay(s, machine);
                        _mirrored?.Invoke(s, machine);
                        return completed;
                    });
                    if (more) { await _remote.SendOwnerBindingAsync(owner, $"{{\"t\":\"next\",\"id\":{id}}}", ct); continue; }
                    cut = null; cutAt = null;
                }
                else
                {
                    try
                    {
                        cursor = _writer.Write("engine.mirror.events", s =>
                        {
                            var completed = MirrorApply.Events(s, machine, frame.GetProperty("after").GetInt64(), through, at,
                                JsonSerializer.Deserialize(frame.GetProperty("events"), EngineJson.Default.IReadOnlyListEngineEvent)!,
                                frame.GetProperty("rows").EnumerateArray().ToArray(), Now);
                            _mirrored?.Invoke(s, machine);
                            return completed;
                        });
                    }
                    catch (MirrorGap)
                    {
                        lock (_gate) LoseReadiness();
                        caughtUp = false;
                        cursor = _writer.Write("engine.mirror.begin", s => MirrorApply.BeginSnapshot(s, machine, Now));
                        break;
                    }
                }
                // Write returned after AtomicWriter committed and published. Only now release the remote page.
                await _remote.SendOwnerBindingAsync(owner, $"{{\"t\":\"ack\",\"id\":{id},\"stream\":\"engine\",\"after\":{through}}}", ct);
                if (!caughtUp && (type == "engine_snapshot" || through >= watermark))
                {
                    if (!await _remote.IsCurrentOwnerBindingAsync(owner, ct)) throw new IOException("Mirror owner changed");
                    lock (_gate)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (_stopping || owner.IsInvalidated) throw new IOException("Mirror owner changed");
                        _live = LiveMirrorBinding.Publish(this, owner, machine, cursor.Generation, _readinessLifetime);
                        caughtUp = true;
                    }
                    CaughtUp?.Invoke();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _remote.SocketOpened -= Opened;
        lock (_gate) { _stopping = true; LoseReadiness(); }
        await _stop.CancelAsync();
        try { await _run; }
        finally
        {
            while (_opens.Reader.TryRead(out var pending)) await pending.FinishAsync();
            await Task.WhenAll(_cancellations);
            _stop.Dispose();
        }
    }
}
