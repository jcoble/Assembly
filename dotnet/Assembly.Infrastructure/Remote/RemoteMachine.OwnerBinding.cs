using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Engine;
using Assembly.Engine.Reading;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private OwnerConnectionBinding? _ownerBinding;
    private long _ownerLifecycle;
    private bool _ownerStopping;
    private readonly List<Task> _ownerCancellations = [];

    internal sealed class OwnerConnectionBinding
    {
        private readonly RemoteMachine _owner;
        private readonly ClientWebSocket _socket;
        private readonly long _lifecycle;
        private readonly CancellationTokenSource _stop = new();
        private int _invalidated;
        private readonly CancellationToken _invalidatedToken;
        internal string Machine { get; }
        internal bool IsInvalidated => Volatile.Read(ref _invalidated) != 0 ||
            _socket.State != WebSocketState.Open || _owner._socketStop.IsCancellationRequested;
        internal CancellationToken Invalidated => _invalidatedToken;
        private OwnerConnectionBinding(RemoteMachine owner, string machine, ClientWebSocket socket, long lifecycle)
        {
            (_owner, Machine, _socket, _lifecycle) = (owner, machine, socket, lifecycle);
            _invalidatedToken = _stop.Token;
        }
        internal static OwnerConnectionBinding Capture(RemoteMachine owner, string machine, ClientWebSocket socket, long lifecycle)
            => new(owner, machine, socket, lifecycle);
        internal bool Matches(RemoteMachine owner, ClientWebSocket socket, long lifecycle)
            => !IsInvalidated && ReferenceEquals(_owner, owner) && ReferenceEquals(_socket, socket) && _lifecycle == lifecycle;
        internal void Revoke() => Interlocked.Exchange(ref _invalidated, 1);
        internal async Task CancelAsync()
        {
            try { await _stop.CancelAsync(); }
            finally { _stop.Dispose(); }
        }
    }

    // The caller must hold _socketSend. Never takes the mirror gate.
    internal ClientWebSocket RequireOwnerBinding(OwnerConnectionBinding binding)
    {
        var socket = RequireEngine(binding.Machine);
        if (_ownerStopping || !ReferenceEquals(binding, _ownerBinding) || !binding.Matches(this, socket, _ownerLifecycle))
            throw Unreachable("machine socket changed", $"{_name}: owning engine connection changed");
        return socket;
    }

    private OwnerConnectionBinding CurrentOwnerBinding(string machine, ClientWebSocket socket)
    {
        if (_ownerStopping || _socketStop.IsCancellationRequested) throw new OperationCanceledException();
        return _ownerBinding ??= OwnerConnectionBinding.Capture(this, machine, socket, _ownerLifecycle);
    }

    internal async Task<OwnerConnectionBinding> CaptureOwnerBindingAsync(string expectedMachine, CancellationToken ct)
    {
        await _socketSend.WaitAsync(ct);
        try
        {
            var socket = RequireEngine(expectedMachine);
            if (_socketStop.IsCancellationRequested) throw new OperationCanceledException(_socketStop.Token);
            var binding = CurrentOwnerBinding(expectedMachine, socket);
            RequireOwnerBinding(binding);
            return binding;
        }
        finally { _socketSend.Release(); }
    }

    internal async Task<bool> IsCurrentOwnerBindingAsync(OwnerConnectionBinding binding, CancellationToken ct)
    {
        if (binding.IsInvalidated) return false;
        await _socketSend.WaitAsync(ct);
        try { RequireOwnerBinding(binding); return true; }
        catch (MachineUnreachable) { return false; }
        finally { _socketSend.Release(); }
    }

    internal async Task<(MachineInfo Info, OwnerConnectionBinding Binding)> ReadMirrorOwnerAsync(CancellationToken ct)
    {
        ClientWebSocket socket;
        await _socketSend.WaitAsync(ct);
        try
        {
            socket = _socket is { State: WebSocketState.Open } current && !_socketStop.IsCancellationRequested && !_ownerStopping
                ? current : throw Unreachable("machine socket closed", $"{_name}: machine socket closed");
        }
        finally { _socketSend.Release(); }
        var result = await CallAsync("engine", "machine", JsonSerializer.Deserialize("{}", EngineJson.Default.JsonElement),
            false, ct, expectedConnection: socket);
        var info = result.Deserialize(EngineSocketJson.Default.MachineInfo) ?? throw new IOException("Invalid engine identity");
        await _socketSend.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(socket, RequireEngine(info.Machine)))
                throw Unreachable("machine socket changed", $"{_name}: owning engine connection changed");
            var binding = CurrentOwnerBinding(info.Machine, socket);
            RequireOwnerBinding(binding);
            return (info, binding);
        }
        finally { _socketSend.Release(); }
    }

    internal async Task SendOwnerBindingAsync(OwnerConnectionBinding binding, string frame, CancellationToken ct)
    {
        await _socketSend.WaitAsync(ct);
        try { await SendFrameAsync(RequireOwnerBinding(binding), frame, ct); }
        finally { _socketSend.Release(); }
    }

    private void InvalidateOwnerBinding()
    {
        _ownerLifecycle++;
        if (_ownerBinding is not { } binding) return;
        _ownerBinding = null;
        binding.Revoke();
        _ownerCancellations.RemoveAll(task => task.IsCompletedSuccessfully);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ownerCancellations.Add(CancelOwnerAfterAsync(start.Task, binding));
        start.SetResult();
    }

    private static async Task CancelOwnerAfterAsync(Task start, OwnerConnectionBinding binding)
    {
        await start;
        await binding.CancelAsync();
    }
}
