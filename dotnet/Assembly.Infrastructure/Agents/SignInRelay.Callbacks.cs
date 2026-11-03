using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Agents;

public sealed partial class SignInRelay : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Machine, string Operation), CallbackScope> _callbacks = [];
    private readonly CancellationTokenSource _closing = new();
    private readonly RemoteConnections _connections;
    private readonly EngineRuntime _runtime;
    private readonly IAppDatabase _db;
    private readonly Func<string, string, int, CancellationToken, Task<LoopbackForward>> _openForward =
        (ssh, host, port, ct) => LoopbackForward.OpenAsync(ssh, host, port, TextWriter.Null, ct);
    private Task? _disposal;

    internal SignInRelay(MachineForward forward, RemoteConnections connections, EngineRuntime runtime, IAppDatabase db,
        Func<string, string, int, CancellationToken, Task<LoopbackForward>>? openForward = null)
    {
        _forward = forward;
        (_connections, _runtime, _db) = (connections, runtime, db);
        if (openForward is not null) _openForward = openForward;
    }

    private async Task<CallbackScope> ScopeAsync(string machine, string operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireActiveOperation(machine, operation, ct);
        lock (_gate)
        {
            _closing.Token.ThrowIfCancellationRequested();
            if (_callbacks.TryGetValue((machine, operation), out var existing)) return existing;
        }
        var target = await _forward.CaptureSignInTargetAsync(machine, ct);
        var captured = await _connections.CaptureSignInCallbackAsync(_db, machine, target.Remote, target.Binding, ct);
        captured.EnsureCurrent();
        ct.ThrowIfCancellationRequested();
        RequireActiveOperation(machine, operation, ct);
        lock (_gate)
        {
            _closing.Token.ThrowIfCancellationRequested();
            if (_callbacks.TryGetValue((machine, operation), out var existing)) return existing;
            ct.ThrowIfCancellationRequested();
            var scope = new CallbackScope(this, machine, operation, target, captured);
            _callbacks.Add((machine, operation), scope);
            return scope;
        }
    }

    private void RequireActiveOperation(string machine, string operation, CancellationToken ct)
    {
        var row = _runtime.Reader.Entity("sign_in", operation, ct, machine);
        if (row is null || row.Value.GetProperty("state").GetString() is not ("starting" or "waiting_for_person" or "finishing"))
            throw Refusal();
    }

    private void RemoveTerminalScope(string machine, string operation, CallbackScope scope)
    {
        lock (_gate)
            if (_callbacks.TryGetValue((machine, operation), out var current) && ReferenceEquals(current, scope))
                _callbacks.Remove((machine, operation));
    }

    internal int RetainedOperationCount { get { lock (_gate) return _callbacks.Count; } }
    internal Task JoinOperationAsync(string machine, string operation)
    {
        lock (_gate) return _callbacks.TryGetValue((machine, operation), out var scope) ? scope.Completion : Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= CloseCallbacksAsync());
    }

    private async Task CloseCallbacksAsync()
    {
        await _closing.CancelAsync();
        CallbackScope[] scopes;
        lock (_gate) scopes = _callbacks.Values.ToArray();
        foreach (var scope in scopes) scope.Revoke();
        await Task.WhenAll(scopes.Select(scope => scope.Completion));
        lock (_gate) _callbacks.Clear();
        _closing.Dispose();
    }

    private sealed class CallbackScope
    {
        private readonly SignInRelay _relay;
        private readonly string _machine, _operation;
        private MachineForward.SignInTarget? _target;
        internal MachineForward.SignInTarget Target => _target ?? throw Failure();
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _stopping;
        private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        private readonly object _state = new();
        private readonly List<TaskCompletionSource<ChallengeReply?>> _waiters = [];
        private IDisposable? _published;
        private EngineMirror? _mirror;
        private CancellationTokenRegistration _ownerLost;
        private CancellationTokenRegistration _liveLost;
        private Action? _ensureCallback;
        private string? _ssh;
        private LoopbackForward? _forward;
        private (string Host, int Port)? _callback;
        private Task? _loss;
        private CancellationTokenSource? _forwardStop;
        private bool _admitted, _terminal;
        private int _revoked;
        private string _problem = "signin_input_failed";
        private long _lastGeneration, _revision, _replyRevision;
        private ChallengeReply? _reply;
        internal Task Completion { get; }

        internal CallbackScope(SignInRelay relay, string machine, string operation, MachineForward.SignInTarget target,
            (string Ssh, EngineMirror.LiveMirrorBinding Live, Action EnsureCurrent) captured)
        {
            (_relay, _machine, _operation, _target) = (relay, machine, operation, target);
            _stop = CancellationTokenSource.CreateLinkedTokenSource(relay._closing.Token, target.Binding.Invalidated);
            _stopping = _stop.Token;
            _ssh = captured.Ssh; _ensureCallback = captured.EnsureCurrent;
            try
            {
                // Existing committed/mirrored engine publication and catch-up only enqueue; no I/O under their gates.
                _published = relay._runtime.PublisherFor(machine).Register(change =>
                {
                    if (change.EntityKind == "sign_in" && change.EntityId == operation) Wake();
                }, out _, Wake);
                _mirror = relay._connections.CaptureMirrorOwners().FirstOrDefault(owner => ReferenceEquals(owner.Machine, target.Remote)).Mirror;
                if (_mirror is not null) _mirror.CaughtUp += Wake;
                _ownerLost = _stopping.Register(Revoke);
                _liveLost = captured.Live.Invalidated.Register(() => { _problem = "source_changed"; Revoke(); });
                captured.EnsureCurrent();
                Completion = Task.Run(RunAsync);
            }
            catch
            {
                _published?.Dispose();
                if (_mirror is not null) _mirror.CaughtUp -= Wake;
                _ownerLost.Dispose(); _liveLost.Dispose(); _stop.Dispose();
                throw;
            }
        }

        private void Wake() { Interlocked.Increment(ref _revision); _signals.Writer.TryWrite(true); }
        internal void Revoke()
        {
            if (_target?.Binding.IsInvalidated == true) _problem = "source_changed";
            Interlocked.Exchange(ref _revoked, 1);
            Wake();
        }

        internal async Task<ChallengeReply?> ReadAsync(CancellationToken ct)
        {
            var waiter = new TaskCompletionSource<ChallengeReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_state)
            {
                if (_terminal) return null;
                EnsureCurrent();
                _waiters.Add(waiter);
            }
            // A reader only joins the retained operation. Closing it never cancels an admitted setup.
            _signals.Writer.TryWrite(false);
            try { return await waiter.Task.WaitAsync(ct); }
            finally { lock (_state) _waiters.Remove(waiter); }
        }

        internal void EnsureInput(long generation)
        {
            lock (_state)
            {
                EnsureCurrent();
                if (!_admitted || _replyRevision != Volatile.Read(ref _revision) || generation != _lastGeneration) throw new CallFailed("The sign-in challenge is no longer available.", "stale_challenge");
                if (_reply?.CallbackHost is not null && (_forward is null || _forward.Lost.IsCompleted)) throw Refusal();
            }
        }

        internal void Guard(CallContext call, bool challenge = false, ChallengeReply? expected = null)
        {
            lock (_state)
            {
                EnsureCurrent();
                if (challenge && !ReferenceEquals(expected, _reply)) throw Refusal();
                call.SourceGuard = new CallbackReplySource(this, challenge ? _replyRevision : -1, call.SourceGuard);
            }
        }

        private CallFailed Failure() => new("Provider sign-in is unavailable.", _problem);

        private void EnsureCurrent()
        {
            if (Volatile.Read(ref _revoked) != 0 || _stopping.IsCancellationRequested) throw Failure();
            Target.EnsureCurrent();
            _ensureCallback?.Invoke();
        }

        private async Task RunAsync()
        {
            Task refresh = Task.CompletedTask;
            CancellationTokenSource? refreshing = null;
            long refreshingRevision = -1;
            try
            {
                while (await _signals.Reader.WaitToReadAsync(_stopping))
                {
                    while (_signals.Reader.TryRead(out _)) { }
                    // Another view joins an in-flight read/open; only actual operation invalidation supersedes it.
                    if (!refresh.IsCompleted && Volatile.Read(ref _revision) == refreshingRevision) continue;
                    refreshing?.Cancel();
                    try { await refresh; } catch (OperationCanceledException) { }
                    refreshing?.Dispose(); refreshing = null;
                    if (Volatile.Read(ref _revoked) != 0) break;
                    refreshing = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
                    refreshingRevision = Interlocked.Increment(ref _revision);
                    refresh = RefreshAsync(refreshingRevision, refreshing.Token);
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            finally
            {
                Interlocked.Exchange(ref _revoked, 1);
                if (refreshing is not null) await refreshing.CancelAsync();
                try { await refresh; } catch (OperationCanceledException) { }
                refreshing?.Dispose();
                await _stop.CancelAsync();
                if (_mirror is not null) _mirror.CaughtUp -= Wake;
                _mirror = null;
                _ownerLost.Dispose(); _liveLost.Dispose(); _ownerLost = default; _liveLost = default;
                await CloseForwardAsync();
                if (!_terminal) await CancelOriginalAttemptAsync();
                Finish(null, Failure());
                lock (_state) { _reply = null; _callback = null; _ensureCallback = null; _target = null; _ssh = null; }
                _stop.Dispose();
                bool terminal;
                try
                {
                    // Unknown/offline cancellation is not terminal proof. Observe the current canonical engine row
                    // through the existing publisher, which survives same-engine mirror replacement/reset.
                    terminal = await WaitForTerminalProofAsync();
                }
                finally { _published?.Dispose(); _published = null; }
                if (terminal) _relay.RemoveTerminalScope(_machine, _operation, this);
            }
        }

        private async Task RefreshAsync(long revision, CancellationToken ct)
        {
            // This token belongs to operation/source/host lifetime and superseding engine observations, never a view.
            var token = ct;
            try
            {
                EnsureCurrent();
                if (_relay._runtime is { } runtime)
                {
                    var row = runtime.Reader.Entity("sign_in", _operation, token, _machine);
                    if (row is null) throw Refusal();
                    var state = row.Value.GetProperty("state").GetString();
                    if (state is "signed_in" or "failed" or "cancelled" or "expired")
                    {
                        lock (_state) _terminal = true;
                        Finish(null, null); Revoke(); return;
                    }
                }
                var call = new CallContext(token, "");
                ChallengeReply? reply;
                try { reply = await _relay._forward.SignInChallengeAsync(_machine, _operation, call, Target); }
                finally { call.SourceGuard?.Dispose(); }
                EnsureRefresh(revision, token);
                if (reply is null)
                {
                    // Consuming a challenge is not completion of its provider callback or engine operation.
                    lock (_state) { _reply = null; _replyRevision = revision; }
                    Finish(null, null); return;
                }
                if (reply.Generation < _lastGeneration) throw Refusal();
                if ((reply.CallbackHost is null) != (reply.CallbackPort is null)) throw Refusal();
                if (reply.CallbackHost is { } host && reply.CallbackPort is { } port)
                {
                    if (_callback != (host, port))
                    {
                        if (_forward is not null) await CloseForwardAsync();
                        EnsureRefresh(revision, token);
                        var opened = await _relay._openForward(_ssh ?? throw Refusal(), host, port, token);
                        try
                        {
                            EnsureRefresh(revision, token);
                            _callback = (host, port); _forward = opened;
                            _forwardStop = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
                            _loss = ObserveLossAsync(opened, _forwardStop.Token);
                        }
                        catch { await opened.DisposeAsync(); throw; }
                    }
                    if (_forward is null || _forward.Lost.IsCompleted) throw Refusal();
                }
                else if (_forward is not null)
                {
                    await CloseForwardAsync(); _callback = null;
                }
                EnsureRefresh(revision, token);
                reply = reply with { Ready = true, LinkLost = false };
                lock (_state) { _lastGeneration = reply.Generation; _reply = reply; _replyRevision = revision; _admitted = true; }
                Finish(reply, null);
            }
            catch (RefreshSuperseded) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && !_stopping.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (error is CallFailed { Reason: "source_changed" }) _problem = "source_changed";
                Revoke(); Finish(null, error is CallFailed failed ? SafeRefusal(failed) : Failure());
            }
        }

        private async Task<bool> WaitForTerminalProofAsync()
        {
            if (_terminal) return true;
            try
            {
                while (!_relay._closing.IsCancellationRequested)
                {
                    while (_signals.Reader.TryRead(out _)) { }
                    try
                    {
                        var row = _relay._runtime.Reader.Entity("sign_in", _operation, _relay._closing.Token, _machine);
                        if (row is not null && row.Value.GetProperty("state").GetString() is "signed_in" or "failed" or "cancelled" or "expired")
                            return true;
                    }
                    catch (Exception error) when (error is KeyNotFoundException or CallFailed or OperationCanceledException or ObjectDisposedException)
                    {
                        // Absent/unreadable is unknown, not evidence that the original attempt ended.
                    }
                    await _signals.Reader.ReadAsync(_relay._closing.Token);
                }
            }
            catch (OperationCanceledException) when (_relay._closing.IsCancellationRequested) { }
            return false;
        }

        private async Task CancelOriginalAttemptAsync()
        {
            try
            {
                Target.EnsureCurrent();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var row = _relay._runtime.Reader.Entity("sign_in", _operation, deadline.Token, _machine);
                if (row is null || row.Value.GetProperty("state").GetString() is not ("starting" or "waiting_for_person" or "finishing")) return;
                await _relay._forward.CancelSignInAsync(Target, _operation, row.Value.GetProperty("version").GetInt64(), deadline.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The original owner could not confirm cancellation. Never retry on a replacement connection.
            }
        }

        private void EnsureRefresh(long revision, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); EnsureCurrent();
            if (revision != Volatile.Read(ref _revision)) throw new RefreshSuperseded();
        }
        private sealed class RefreshSuperseded : Exception;

        private async Task CloseForwardAsync()
        {
            if (_forwardStop is not null) await _forwardStop.CancelAsync();
            if (_forward is not null) await _forward.DisposeAsync();
            if (_loss is not null) await _loss;
            _forwardStop?.Dispose(); _forwardStop = null; _loss = null; _forward = null;
        }

        private async Task ObserveLossAsync(LoopbackForward forward, CancellationToken ct)
        {
            try
            {
                await forward.Lost.WaitAsync(ct);
                if (ReferenceEquals(_forward, forward)) Revoke();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        private void Finish(ChallengeReply? reply, Exception? error)
        {
            TaskCompletionSource<ChallengeReply?>[] waiters;
            lock (_state) { waiters = _waiters.ToArray(); _waiters.Clear(); }
            foreach (var waiter in waiters)
                if (error is null) waiter.TrySetResult(reply); else waiter.TrySetException(error);
        }

        private sealed class CallbackReplySource(CallbackScope scope, long revision, IExpectedSourceGuard? inner) : IExpectedSourceGuard
        {
            public CancellationToken Changed => scope._stopping;
            public void EnsureCurrent()
            {
                scope.EnsureCurrent();
                if (revision >= 0 && revision != Volatile.Read(ref scope._revision)) throw Refusal();
                inner?.EnsureCurrent();
            }
            public void Dispose() => inner?.Dispose();
        }
    }
}
