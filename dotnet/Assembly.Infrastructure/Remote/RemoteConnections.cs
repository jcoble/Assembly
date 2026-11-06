using Assembly.Application.Common.Database;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Engine;
using System.Text.Json;
using System.Collections.Concurrent;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Remote;

/// <summary>Owns each saved machine's connection, copied rows and helper delivery for this backend lifetime.</summary>
internal sealed partial class RemoteConnections : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly MachineStore _store;
    private readonly IAtomicWriter _writer;
    private readonly EngineRuntime? _acceptanceEngine;
    private readonly MachineSet _machines;
    private readonly MachineStatus _status;
    private readonly LiveHub _hub;
    private readonly SessionStore _sessions;
    private readonly TextWriter _log;
    private readonly Action<string>? _turnEnded;
    private readonly Action<IWriteScope, string>? _mirrored;
    private readonly MachinesFeature _feature;
    private readonly Func<SavedMachine, CancellationToken, Task<(RemoteConfig Config, IAsyncDisposable? Tunnel)>> _open;
    private readonly global::Assembly.Infrastructure.Topics.Topics _topics;
    private readonly global::Assembly.Infrastructure.Topics.Topics.Subscription _subscription;
    private readonly Dictionary<string, Task> _pending = [];
    // Includes running work and every waiter, independently of the queue tail.
    private readonly Dictionary<string, HashSet<CancellationTokenSource>> _operations = [];
    private readonly ConcurrentDictionary<string, (RemoteMachine Machine, EngineMirror Mirror, IAsyncDisposable? Tunnel, MachineStatus Status, RemoteConfig Config, SavedMachine Profile)> _active = new();
    private sealed class CliRefresh
    {
        internal readonly CancellationTokenSource Stop = new();
        internal Task Work = Task.CompletedTask;
        internal bool Requested;
        internal bool Running;
    }
    private readonly Dictionary<string, CliRefresh> _cliRefresh = [];
    private bool _disposed;

    public RemoteConnections(MachineStore store, IAtomicWriter writer, MachineSet machines, MachineStatus status, LiveHub hub, SessionStore sessions,
        global::Assembly.Infrastructure.Topics.Topics topics, TextWriter log, MachinesFeature feature,
        Action<string>? turnEnded = null,
        Func<SavedMachine, CancellationToken, Task<(RemoteConfig Config, IAsyncDisposable? Tunnel)>>? open = null,
        Action<IWriteScope, string>? mirrored = null, EngineRuntime? acceptanceEngine = null)
    {
        (_store, _machines, _status, _hub, _sessions, _topics, _log, _feature, _turnEnded) =
            (store, machines, status, hub, sessions, topics, log, feature, turnEnded);
        _writer = writer;
        _acceptanceEngine = acceptanceEngine;
        _mirrored = mirrored;
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

    internal RemoteMachine? ByName(string name) => _active.TryGetValue(name, out var active) ? active.Machine : null;

    internal RemoteMachine? ByEngineMachine(IAppDatabase db, string machine)
    {
        var name = db.QueryFirst("SELECT name FROM engine_mirror WHERE machine=$machine AND state<>'retired'",
            row => row.Text(0), CancellationToken.None, new SqlArg("$machine", machine))
            ?? throw new KeyNotFoundException("Unknown remote engine machine");
        return _active.TryGetValue(name, out var active) ? active.Machine
            : throw Disconnected(name);
    }

    internal static MachineUnreachable Disconnected(string name) => new(name, $"{name}: machine socket is disconnected");

    internal RemoteCliProfile? CliLocator(IAppDatabase db, string machine, CancellationToken ct)
    {
        var name = db.QueryFirst("SELECT name FROM engine_mirror WHERE machine=$machine AND state<>'retired'",
            row => row.Text(0), ct, new SqlArg("$machine", machine));
        if (name is null) return null;
        var saved = _store.GetCliLocator(name, ct);
        return saved?.Locator.MachineId == machine ? saved : null;
    }

    private void RequestCliLocator(string name, RemoteMachine remote, EngineMirror mirror, MachineProfileSnapshot profile, CliRefresh refresh)
    {
        lock (_gate)
        {
            if (_disposed || refresh.Stop.IsCancellationRequested) return;
            refresh.Requested = true;
            if (refresh.Running) return;
            refresh.Running = true;
            refresh.Work = Task.Run(async () =>
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (!refresh.Requested || refresh.Stop.IsCancellationRequested)
                        { refresh.Running = false; return; }
                        refresh.Requested = false;
                    }
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        using var stop = CancellationTokenSource.CreateLinkedTokenSource(refresh.Stop.Token, deadline.Token);
                        var live = await mirror.CaptureLiveBindingAsync(stop.Token);
                        if (live is null) continue;
                        using var ownerStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, live.Invalidated);
                        var locator = await remote.ReadCliLocatorAsync(live.OwnerBinding, ownerStop.Token);
                        if (!await mirror.IsCurrentLiveBindingAsync(live, ownerStop.Token)) continue;
                        _writer.Write("machines.cli-locator", scope =>
                        {
                            ownerStop.Token.ThrowIfCancellationRequested();
                            return !live.IsInvalidated && _active.TryGetValue(name, out var active) && ReferenceEquals(active.Machine, remote)
                                && _store.SetCliLocator(scope, profile, locator);
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception) { _log.WriteLine($"{name}: CLI locator unavailable"); }
                }
            });
        }
    }

    internal IReadOnlyList<(RemoteMachine Machine, EngineMirror Mirror)> CaptureMirrorOwners() =>
        _active.Values.Select(active => (active.Machine, active.Mirror)).ToArray();

    internal async Task<(RemoteMachine Machine, EngineMirror.LiveMirrorBinding Live, Action EnsureCurrent)>
        CaptureSessionOpenSourceAsync(IAppDatabase db, ExpectedSource source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.Generation <= 0 || source.RouteMachine is not { Length: > 0 } name || db.QueryFirst("""
                SELECT COUNT(*) FROM engine_mirror WHERE machine=$machine AND name=$name
                    AND generation=$generation AND state='live'
                """, row => row.Long(0), ct, new SqlArg("$machine", source.Machine), new("$name", name),
                new("$generation", source.Generation)) != 1 || !_active.TryGetValue(name, out var captured))
            throw new CallFailed("The selected row source changed.", "source_changed");
        var live = await captured.Mirror.CaptureLiveBindingAsync(ct);
        if (live is null || live.Machine != source.Machine || live.Generation != source.Generation)
            throw new CallFailed("The selected row source changed.", "source_changed");
        void EnsureCurrent()
        {
            if (live.IsInvalidated || !_active.TryGetValue(name, out var current) ||
                !ReferenceEquals(current.Machine, captured.Machine) || !ReferenceEquals(current.Mirror, captured.Mirror) ||
                !ReferenceEquals(_machines.Named(name), captured.Machine))
                throw new CallFailed("The selected row source changed.", "source_changed");
        }
        EnsureCurrent();
        return (captured.Machine, live, EnsureCurrent);
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
                    var followed = _active.TryGetValue(name, out var prior) ? prior.Machine.FollowedSessions : [];
                    await CleanupAsync(name);
                    var capturedProfile = _store.CaptureProfile(name, stop.Token);
                    var profile = capturedProfile?.Profile;
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
                        var remote = new RemoteMachine(name, opened.Config.Address, opened.Config.Token, _hub, _sessions, _log, scoped, _turnEnded, _topics,
                            connectionIdentity: $"{profile.Ssh.Length}:{profile.Ssh}{profile.Port}:{profile.TokenFile.Length}:{profile.TokenFile}", acceptanceEngine: _acceptanceEngine);
                        if (tunnel is RemoteTunnel returning) returning.Forwarding += remote.ConnectNow;
                        var mirror = new EngineMirror(remote, _writer, TimeProvider.System, _log, _mirrored);
                        var cli = new CliRefresh();
                        lock (_gate) _cliRefresh[name] = cli;
                        mirror.CaughtUp += () => RequestCliLocator(name, remote, mirror, capturedProfile!, cli);
                        _active[name] = (remote, mirror, tunnel, scoped, opened.Config with { Name = name }, profile);
                        tunnel = null;
                        _machines.Set(remote);
                        remote.WatchRunning(followed.Concat(_store.RunningTurns(name)), _store.RunningHelpers(name));
                        if (probe) await _feature.ConnectAsync(remote, stop.Token);
                        else
                            try { await remote.ProbeAsync(stop.Token); }
                            catch (MachineUnreachable) { }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (Exception error) { scoped.Unreachable(name, error.Message); }
                    finally { if (tunnel is not null) await tunnel.DisposeAsync(); }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
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
                CliRefresh? cli;
                lock (_gate) _cliRefresh.Remove(name, out cli);
                if (cli is not null)
                {
                    await cli.Stop.CancelAsync();
                    try { await cli.Work; }
                    finally { cli.Stop.Dispose(); }
                }
                try { await old.Mirror.DisposeAsync(); }
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
