using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private readonly object _relayLeaseGate = new();
    private readonly HashSet<Task> _relayLeaseOwners = [];
    private bool _relayLeaseStopping;
    private RelayLeaseLifetime EnterRelayLease()
    {
        lock (_relayLeaseGate)
        {
            if (_relayLeaseStopping) throw new CallFailed("relay_connection_changed");
            var lifetime = new RelayLeaseLifetime(this, _socketStop.Token);
            _relayLeaseOwners.Add(lifetime.Completion.Task); return lifetime;
        }
    }
    private Task[] StopRelayLeases()
    {
        lock (_relayLeaseGate) { _relayLeaseStopping = true; return _relayLeaseOwners.ToArray(); }
    }
    private sealed class RelayLeaseLifetime(RemoteMachine owner, CancellationToken stopping)
    {
        internal readonly CancellationToken Stopping = stopping;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete()
        {
            lock (owner._relayLeaseGate) owner._relayLeaseOwners.Remove(Completion.Task);
            Completion.TrySetResult();
        }
    }

    internal Task<RelayBulkLease> AcquireRelayBulkAsync(OwnerConnectionBinding binding, CancellationToken ct, EngineMirror.LiveMirrorBinding? live = null) =>
        RelayBulkLease.AcquireAsync(this, binding, ct, live);

    /// <summary>Owns the existing bulk semaphore for one complete transfer, with no reconnect in that transfer.</summary>
    internal sealed class RelayBulkLease : IAsyncDisposable
    {
        private readonly RemoteMachine _owner;
        private readonly OwnerConnectionBinding _binding;
        private readonly EngineMirror.LiveMirrorBinding? _live;
        private readonly RelayLeaseLifetime _lifetime;
        private readonly SemaphoreSlim _exchange = new(1);
        private ClientWebSocket? _bulk;
        private bool _failed;
        private int _disposed;
        private RelayBulkLease(RemoteMachine owner, OwnerConnectionBinding binding, EngineMirror.LiveMirrorBinding? live, RelayLeaseLifetime lifetime)
            => (_owner, _binding, _live, _lifetime) = (owner, binding, live, lifetime);
        internal string Machine => _binding.Machine;
        internal static async Task<RelayBulkLease> AcquireAsync(RemoteMachine owner, OwnerConnectionBinding binding, CancellationToken ct, EngineMirror.LiveMirrorBinding? live)
        {
            var lifetime = owner.EnterRelayLease();
            try
            {
                if (binding.IsInvalidated) throw new CallFailed("relay_connection_changed");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, binding.Invalidated, lifetime.Stopping);
                ct = linked.Token;
                await owner._bulkCall.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await owner._socketSend.WaitAsync(ct).ConfigureAwait(false);
                    try { owner.RequireOwnerBinding(binding); RequireLiveMirror(binding, live); return new(owner, binding, live, lifetime); }
                    finally { owner._socketSend.Release(); }
                }
                catch { owner._bulkCall.Release(); throw; }
            }
            catch { lifetime.Complete(); throw; }
        }

        internal async Task<JsonElement> CallAsync(string method, JsonElement args, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_binding.IsInvalidated) throw new CallFailed("relay_connection_changed");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _binding.Invalidated, _lifetime.Stopping);
            ct = linked.Token;
            await _exchange.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (_failed) throw new CallFailed("relay_connection_changed");
                ClientWebSocket socket;
                await _owner._socketSend.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    socket = _owner.RequireOwnerBinding(_binding);
                    RequireLiveMirror(_binding, _live);
                    if (_bulk is not null && (!ReferenceEquals(_bulk, _owner._bulkSocket) || _bulk.State != WebSocketState.Open))
                        throw new CallFailed("relay_connection_changed");
                }
                finally { _owner._socketSend.Release(); }
                // Fixed method names are chosen only by the coordinator; measure the complete existing call frame.
                var estimate = "{\"t\":\"call\",\"id\":-9223372036854775808,\"feature\":\"engine\",\"method\":\"" + method + "\",\"args\":" + args.GetRawText() + "}";
                if (Encoding.UTF8.GetByteCount(estimate) > 4 * 1024 * 1024) throw new CallFailed("frame_too_large");
                var result = await _owner.BulkCallHeldAsync("engine", method, args, ct, _binding.Machine, socket, _binding, _live).ConfigureAwait(false);
                await _owner._socketSend.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    _owner.RequireOwnerBinding(_binding);
                    RequireLiveMirror(_binding, _live);
                    _bulk ??= _owner._bulkSocket;
                    if (_bulk is null || !ReferenceEquals(_bulk, _owner._bulkSocket) || _bulk.State != WebSocketState.Open)
                        throw new CallFailed("relay_connection_changed");
                }
                finally { _owner._socketSend.Release(); }
                return result;
            }
            catch { _failed = true; throw; }
            finally { _exchange.Release(); }
        }
        // Used only when this transfer's explicit drop/close cannot reach its exact connection.
        internal async Task AbortAsync()
        {
            await _exchange.WaitAsync().ConfigureAwait(false);
            try
            {
                _failed = true;
                await _owner._socketSend.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_bulk is not null && ReferenceEquals(_bulk, _owner._bulkSocket))
                    {
                        _bulk.Abort(); _bulk.Dispose(); _owner._bulkSocket = null; _owner._bulkEngineIdentity = null;
                    }
                }
                finally { _owner._socketSend.Release(); }
            }
            finally { _exchange.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _exchange.WaitAsync().ConfigureAwait(false);
            try { _owner._bulkCall.Release(); }
            finally { _exchange.Release(); _lifetime.Complete(); }
        }
    }
}
