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
    private readonly Dictionary<string, (Task Work, CancellationTokenSource Stop)> _pending = [];
    private readonly ConcurrentDictionary<string, (RemoteMachine Machine, RemoteOutbox Outbox, IAsyncDisposable? Tunnel, MachineStatus Status)> _active = new();
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

    internal Task ReadyAsync(string name) { lock (_gate) return _pending[name].Work; }

    private Task Queue(string name, bool connect, bool probe = false, CancellationToken cancel = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            var previous = _pending.GetValueOrDefault(name);
            if (!connect || !probe) previous.Stop?.Cancel();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var work = Task.Run(async () =>
            {
                if (previous.Work is not null) await previous.Work;
                previous.Stop?.Dispose();
                if (connect && probe && _active.TryGetValue(name, out var active))
                {
                    try { await _feature.ConnectAsync(active.Machine, stop.Token); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    return;
                }
                await _feature.StopProbesAsync(name);
                if (_active.TryRemove(name, out var old))
                {
                    old.Status.Stop();
                    await old.Outbox.DisposeAsync();
                    await old.Machine.DisposeAsync();
                    if (old.Tunnel is not null) await old.Tunnel.DisposeAsync();
                }
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
                    _active[name] = (remote, outbox, tunnel, scoped);
                    tunnel = null;
                    _machines.Set(remote);
                    remote.WatchRunning(_store.RunningTurns(name), _store.RunningHelpers(name));
                    if (probe) await _feature.ConnectAsync(remote, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error) { scoped.Unreachable(name, error.Message); }
                finally { if (tunnel is not null) await tunnel.DisposeAsync(); }
            }, CancellationToken.None); // Cleanup must run even if the queued operation was cancelled.
            _pending[name] = (work, stop);
            return work;
        }
    }

    private async Task<(RemoteConfig, IAsyncDisposable?)> OpenAsync(SavedMachine profile, CancellationToken cancel)
    {
        var opened = await RemoteTunnel.ConnectAsync(profile.Ssh, profile.Port, profile.TokenFile, _log, cancel);
        // The config name is the SSH destination; sessions use the saved stable name instead.
        return (opened.Config, opened.Tunnel);
    }

    public async ValueTask DisposeAsync()
    {
        _topics.Unsubscribe(_subscription);
        Task[] tasks;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pending in _pending.Values) pending.Stop.Cancel();
            tasks = [.. _pending.Values.Select(pending => pending.Work)];
        }
        await Task.WhenAll(tasks);
        foreach (var (name, active) in _active)
        {
            await _feature.StopProbesAsync(name);
            active.Status.Stop();
            await active.Outbox.DisposeAsync();
            await active.Machine.DisposeAsync();
            if (active.Tunnel is not null) await active.Tunnel.DisposeAsync();
        }
        foreach (var pending in _pending.Values) pending.Stop.Dispose();
    }
}
