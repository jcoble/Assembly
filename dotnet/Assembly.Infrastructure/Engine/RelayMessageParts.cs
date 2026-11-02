using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Engine;

/// <summary>Owns the bounded relay staging lifetime, including work that has not published a handle.</summary>
internal sealed partial class RelayMessageParts : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _connections = [];
    private readonly HashSet<Task> _cleanups = [];
    private bool _stopping;

    internal Operation Enter(CallContext call)
    {
        if (call.Peer is not OwnerPeer || !call.Closed.CanBeCanceled) throw new CallFailed("forbidden");
        Operation operation;
        lock (_gate)
        {
            if (_stopping || call.Closed.IsCancellationRequested) throw new OperationCanceledException(call.Closed);
            if (!_connections.TryGetValue(call.Connection, out var connection))
                _connections.Add(call.Connection, connection = new(call.Closed));
            if (connection.ClosedToken != call.Closed) throw new CallFailed("bad_connection");
            _cleanups.RemoveWhere(task => task.IsCompletedSuccessfully);
            if (connection.Closed) throw new OperationCanceledException(call.Closed);
            var registerClosed = !connection.Registered;
            connection.Registered = true;
            operation = new(this, connection, call, registerClosed);
            connection.Operations.Add(operation);
        }
        // Registration can synchronously close the connection; publication always checks again.
        operation.Register();
        return operation;
    }

    internal void Closed(string id)
    {
        Connection? connection;
        lock (_gate) _connections.TryGetValue(id, out connection);
        if (connection is not null) Close(connection);
    }

    private void Close(Connection connection)
    {
        TaskCompletionSource<Task> ready;
        lock (_gate)
        {
            if (connection.Closed) return;
            connection.Closed = true;
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var operations = connection.Operations.Select(o => o.Completion.Task).ToArray();
            var cleanup = Cleanup(connection, operations, ready.Task);
            _cleanups.Add(cleanup);
        }
        // Cancellation callbacks and disposal must never run under the feature gate.
        try { ready.SetResult(connection.Stop.CancelAsync()); }
        catch (Exception error) { ready.SetResult(Task.FromException(error)); }
    }

    private static async Task Cleanup(Connection connection, Task[] operations, Task<Task> ready)
    {
        try { await await ready.ConfigureAwait(false); } catch { /* Still join and dispose every owner. */ }
        try { await Task.WhenAll(operations).ConfigureAwait(false); } catch { /* Completion fences are authoritative. */ }
        foreach (var registration in connection.Registrations) registration.Dispose();
        connection.Source?.Dispose();
        connection.Target?.Dispose();
        connection.Source = null; connection.Target = null; connection.Registrations.Clear();
        connection.Stop.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Connection[] connections;
        lock (_gate) { _stopping = true; connections = _connections.Values.ToArray(); }
        foreach (var connection in connections) Close(connection);
        Task[] cleanup;
        lock (_gate) cleanup = _cleanups.ToArray();
        await Task.WhenAll(cleanup).ConfigureAwait(false);
    }

    internal sealed class Connection(CancellationToken closedToken)
    {
        internal readonly CancellationTokenSource Stop = new();
        internal readonly HashSet<Operation> Operations = [];
        internal readonly List<CancellationTokenRegistration> Registrations = [];
        internal readonly CancellationToken ClosedToken = closedToken;
        internal bool Registered;
        internal bool Closed;
        internal SourceStage? Source;
        internal TargetStage? Target;
    }

    internal sealed partial class Operation : IDisposable
    {
        private readonly RelayMessageParts _owner;
        private readonly Connection _connection;
        private readonly CallContext _call;
        private CancellationTokenSource? _linked;
        private bool _disposed;
        private readonly bool _registerClosed;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Operation(RelayMessageParts owner, Connection connection, CallContext call, bool registerClosed)
        { _owner = owner; _connection = connection; _call = call; _registerClosed = registerClosed; }
        internal void Register()
        {
            if (_registerClosed)
            {
                var registration = _call.Closed.Register(static state =>
                {
                    var (owner, connection) = ((RelayMessageParts, Connection))state!;
                    owner.Close(connection);
                }, (_owner, _connection));
                lock (_owner._gate) _connection.Registrations.Add(registration);
            }
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_call.Cancellation, _call.Closed, _connection.Stop.Token);
        }
        internal CancellationToken Token => _linked!.Token;
        private void RequireLive()
        {
            Token.ThrowIfCancellationRequested();
            if (_owner._stopping || _connection.Closed || _call.Closed.IsCancellationRequested)
                throw new OperationCanceledException(Token);
        }
        internal T Return<T>(T value)
        {
            lock (_owner._gate) { RequireLive(); return value; }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _linked?.Dispose();
            lock (_owner._gate) _connection.Operations.Remove(this);
            Completion.TrySetResult();
        }
    }
}
