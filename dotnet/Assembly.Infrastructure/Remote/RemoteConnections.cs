using System.Text.Json;
using System.Collections.Concurrent;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Remote;

/// <summary>Owns each saved machine's connection, copied rows and helper delivery for this backend lifetime.</summary>
internal sealed class RemoteConnections : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly MachineStore _store;
    private readonly MachineSet _machines;
    private readonly MachineStatus _status;
    private readonly LiveHub _hub;
    private readonly SessionStore _sessions;
    private readonly TextWriter _log;
    private readonly Action<string>? _turnEnded;
    private readonly MachinesFeature _feature;
    private readonly Func<SavedMachine, CancellationToken, Task<(RemoteConfig Config, IAsyncDisposable? Tunnel)>> _open;
    private readonly global::Assembly.Infrastructure.Topics.Topics _topics;
    private readonly global::Assembly.Infrastructure.Topics.Topics.Subscription _subscription;
    private readonly Dictionary<string, Task> _pending = [];
    // Includes running work and every waiter, independently of the queue tail.
    private readonly Dictionary<string, HashSet<CancellationTokenSource>> _operations = [];
    private readonly ConcurrentDictionary<string, (RemoteMachine Machine, RemoteOutbox Outbox, IAsyncDisposable? Tunnel, MachineStatus Status, RemoteConfig Config)> _active = new();
    private bool _disposed;

    public RemoteConnections(MachineStore store, MachineSet machines, MachineStatus status, LiveHub hub, SessionStore sessions,
        global::Assembly.Infrastructure.Topics.Topics topics, TextWriter log, MachinesFeature feature,
        Action<string>? turnEnded = null,
        Func<SavedMachine, CancellationToken, Task<(RemoteConfig Config, IAsyncDisposable? Tunnel)>>? open = null)
    {
        (_store, _machines, _status, _hub, _sessions, _topics, _log, _feature, _turnEnded) =
            (store, machines, status, hub, sessions, topics, log, feature, turnEnded);
        _store.Status = status;
        _open = open ?? OpenAsync;
        // A private commit notice avoids counting this owner as a window watching machines (which keeps probes awake).
        // This callback only queues work: no SSH, database read or await under Topics' lock.
        _subscription = topics.Subscribe(MachineStore.Changes, null, _ => { }, (name, json) =>
        {
            if (name is null) return;
            var profile = JsonSerializer.Deserialize(json, SessionsJson.Default.SavedMachine);
            _machines.Remove(name);
            _ = Queue(name, profile is not null);
        });
    }

    public void Start(SavedMachine? import = null)
    {
        if (import is not null)
            // Explicit startup flags win each time, through the same committed profile notification as a socket save.
            _store.Import(import);
        foreach (var profile in _store.All(CancellationToken.None))
        {
            lock (_gate)
                if (_pending.ContainsKey(profile.Name)) continue;
            _ = Queue(profile.Name, true);
        }
    }

    public Task ConnectionAsync(string name, bool connect, CancellationToken cancel)
    {
        if (_store.Get(name, cancel) is null) throw new Assembly.Application.Common.Calls.CallFailed("unknown remote machine");
        return Queue(name, connect, probe: true, cancel).WaitAsync(cancel);
    }

    internal Task ReadyAsync(string name) { lock (_gate) return _pending[name]; }

    /// <summary>The connection already open at startup; subsequent connection changes remain owned here.</summary>
    internal (RemoteMachine Machine, RemoteConfig Config, Action Unreachable)? StartupSnapshot(string? name)
    {
        if (name is null || !_active.TryGetValue(name, out var active)) return null;
        return (active.Machine, active.Config, () => active.Status.Unreachable(name, "Board connection closed"));
    }

    private Task Queue(string name, bool connect, bool probe = false, CancellationToken cancel = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            var previous = _pending.GetValueOrDefault(name);
            if (!_operations.TryGetValue(name, out var operations)) _operations[name] = operations = [];
            if (!connect || !probe)
                foreach (var operation in operations) operation.Cancel();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            operations.Add(stop);
            var work = Task.Run(async () =>
            {
                try
                {
                    if (previous is not null)
                        try { await previous; }
                        catch (Exception error) { _log.WriteLine($"{name} previous connection: {error.Message}"); }
                    if (stop.IsCancellationRequested) return;
                    if (connect && probe && _active.TryGetValue(name, out var active))
                    {
                        if (active.Tunnel is RemoteTunnel activeTunnel) activeTunnel.TryNow();
                        try { await _feature.ConnectAsync(active.Machine, stop.Token); }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                        return;
                    }
                    await CleanupAsync(name);
                    var profile = _store.Get(name, CancellationToken.None);
                    if (profile is null) { _status.Remove(name); return; }
                    _status.Profile(profile);
                    var scoped = _status.Scope(name);
                    if (!connect) { scoped.Unreachable(name, "disconnected"); return; }
                    if (stop.IsCancellationRequested) return;
                    IAsyncDisposable? tunnel = null;
                    try
                    {
                        var opened = await _open(profile, stop.Token);
                        tunnel = opened.Tunnel;
                        stop.Token.ThrowIfCancellationRequested();
                        if (tunnel is RemoteTunnel ssh) ssh.Exited += () => scoped.Unreachable(name, "ssh tunnel exited");
                        var remote = new RemoteMachine(name, opened.Config.Address, opened.Config.Token, _hub, _sessions, _log, scoped, _turnEnded);
                        var outbox = new RemoteOutbox(remote, _sessions, _hub, _machines.Named(null)!, _log);
                        _active[name] = (remote, outbox, tunnel, scoped, opened.Config with { Name = name });
                        tunnel = null;
                        _machines.Set(remote);
                        remote.WatchRunning(_store.RunningTurns(name), _store.RunningHelpers(name));
                        if (probe) await _feature.ConnectAsync(remote, stop.Token);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (Exception error) { scoped.Unreachable(name, error.Message); }
                    finally { if (tunnel is not null) await tunnel.DisposeAsync(); }
                }
                finally
                {
                    lock (_gate)
                    {
                        operations.Remove(stop);
                        stop.Dispose();
                    }
                }
            }, CancellationToken.None); // Cleanup must run even if the queued operation was cancelled.
            _pending[name] = work;
            return work;
        }
    }

    private async Task<(RemoteConfig, IAsyncDisposable?)> OpenAsync(SavedMachine profile, CancellationToken cancel)
    {
        var opened = await RemoteTunnel.ConnectAsync(profile.Ssh, profile.Port, profile.TokenFile, _log, cancel);
        // The config name is the SSH destination; sessions use the saved stable name instead.
        return (opened.Config, opened.Tunnel);
    }

    private async Task CleanupAsync(string name)
    {
        try { await _feature.StopProbesAsync(name); }
        finally
        {
            if (_active.TryRemove(name, out var old))
            {
                old.Status.Stop();
                try { await old.Outbox.DisposeAsync(); }
                finally
                {
                    try { await old.Machine.DisposeAsync(); }
                    finally { if (old.Tunnel is not null) await old.Tunnel.DisposeAsync(); }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _topics.Unsubscribe(_subscription);
        Task[] tasks;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var operations in _operations.Values)
                foreach (var operation in operations) operation.Cancel();
            tasks = [.. _pending.Values];
        }
        List<Exception> errors = [];
        try { await Task.WhenAll(tasks); }
        catch (Exception error) { errors.Add(error); }
        foreach (var name in _active.Keys)
            try { await CleanupAsync(name); }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
