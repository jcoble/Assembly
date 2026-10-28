using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// The machines a new session can run on, over the socket (plan C1): the <c>machines</c> topic sends each one and
/// whether it answered last time, and <c>machines/agents</c> answers the agents a machine (this one when left out) can
/// run. While the topic has a subscriber, the backend clock's ticks probe each remote: on the first tick after a
/// subscribe, then every 15th. <c>machines/connect</c> is the window's Connect button: it starts the ssh tunnel again
/// at once if it's waiting to, and probes that remote now, again while nothing answers (the tunnel may be starting),
/// for up to 10 seconds; it answers the machine's item.
/// </summary>
internal sealed class MachinesFeature : IFeature, IAsyncDisposable
{
    public const string Feature = "machines";
    public const string Topic = "machines";

    private const int Every = 15;
    private static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectAgain = TimeSpan.FromMilliseconds(500);

    private readonly MachineSet _machines;
    private readonly MachineStatus _status;
    private readonly Action _startClock;
    private readonly TimeProvider _time;
    private readonly Action _tryTunnel;
    private readonly MachineStore? _store;
    private readonly Func<string, bool, CancellationToken, Task>? _connection;
    private readonly Dictionary<string, CancellationTokenSource> _probeStops = [];
    // Each remote's last probe, run off the tick's thread; a remote whose probe is still out gets no second. Guarded
    // by itself, as are _stop, _ticks and _disposed.
    private readonly Dictionary<string, Task> _probes = [];
    private CancellationTokenSource _stop = new();
    private int _ticks = Every - 1;
    private bool _disposed;

    /// <param name="startClock">Starts the backend clock, whose ticks come back through <see cref="Tick"/>.</param>
    /// <param name="tryTunnel">Starts the remote's ssh tunnel again now if it's waiting to.</param>
    public MachinesFeature(MachineSet machines, MachineStatus status, Action startClock, TimeProvider time, Action? tryTunnel = null, MachineStore? store = null,
        Func<string, bool, CancellationToken, Task>? connection = null)
    {
        _store = store;
        if (store is not null) store.Status = status;
        _connection = connection;
        _machines = machines;
        _status = status;
        _startClock = startClock;
        _time = time;
        _tryTunnel = tryTunnel ?? (() => { });
    }

    public void Register(ICallRegistry calls)
    {
        calls.Topic(Topic, SessionsJson.Default.MachineItem, item => item.Name ?? "local", (key, _) =>
        {
            if (key is not null)
                throw new CallFailed("machines has no keys");
            // The first tick after a subscribe probes.
            lock (_probes)
                _ticks = Every - 1;
            _startClock();
            return _store is null ? _status.Items : [_status.Items[0], .. _store.All(CancellationToken.None).Select(profile =>
                (_status.Items.FirstOrDefault(item => item.Name == profile.Name) ?? MachineStore.Item(profile)) with
                { Ssh = profile.Ssh, Port = profile.Port, TokenFile = profile.TokenFile })];
        });
        if (_store is not null)
        {
            calls.Write(Feature, "set", SessionsJson.Default.SavedMachine, SessionsJson.Default.MachineItem, (args, _) =>
                Task.FromResult<Func<Assembly.Application.Common.Database.IWriteScope, MachineItem>>(scope =>
                {
                    _store.Set(scope, args);
                    return MachineStore.Item(args);
                }), args => "machine:" + args.Name);
            calls.Write(Feature, "remove", SessionsJson.Default.NameArgs, SessionsJson.Default.Done, (args, _) =>
                Task.FromResult<Func<Assembly.Application.Common.Database.IWriteScope, SendFeature.Done>>(scope =>
                {
                    _store.Remove(scope, args.Name);
                    return new(true);
                }), args => "machine:" + args.Name);
            calls.Read(Feature, "disconnect", SessionsJson.Default.NameArgs, SessionsJson.Default.MachineItem, async (args, call) =>
            {
                await _connection!(args.Name, false, call.Cancellation);
                return _status.Items.First(item => item.Name == args.Name);
            });
        }
        calls.Read(Feature, "agents", SessionsJson.Default.AgentsArgs, SessionsJson.Default.AgentList, async (args, call) =>
        {
            // An empty name is this machine, as GET /agents?machine= takes it.
            var machine = _machines.Named(args.Machine is { Length: > 0 } name ? name : null) ?? throw new CallFailed("unknown machine");
            return new AgentList([.. (await machine.AgentsAsync(call.Cancellation)).Select(agent => new Agent(agent.Id, agent.Name))]);
        });
        // A read: it writes nothing here, so its up to 10 seconds hold up none of the socket's writes.
        calls.Read(Feature, "connect", SessionsJson.Default.ConnectArgs, SessionsJson.Default.MachineItem, async (args, call) =>
        {
            if (_connection is not null)
            {
                await _connection(args.Machine, true, call.Cancellation);
                return _status.Items.First(item => item.Name == args.Machine);
            }
            var machine = _machines.Remotes.FirstOrDefault(remote => remote.Name == args.Machine) ?? throw new CallFailed("unknown remote machine");
            _tryTunnel();
            await ConnectAsync(machine, call.Cancellation);
            return _status.Items.First(item => item.Name == machine.Name);
        });
    }

    internal async Task ConnectAsync(IMachine machine, CancellationToken cancel)
    {
        var status = _status.ForProbe(machine.Name!);
        using var limit = new CancellationTokenSource(ConnectLimit, _time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancel, limit.Token);
        try
        {
            while (true)
            {
                try
                {
                    await machine.ProbeAsync(either.Token);
                    return;
                }
                // An answer it can't use (another row format, a refused token) won't change by asking again.
                catch (MachineUnreachable error) when (error.InnerException is HttpRequestException)
                {
                }
                await Task.Delay(ConnectAgain, _time, either.Token);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            status.Unreachable(machine.Name!, "didn't answer");
        }
        catch (MachineUnreachable)
        {
            // The machine recorded what it saw.
        }
    }

    /// <summary>Each second of the backend clock, with whether <c>machines</c> has a subscriber. Nobody left watching cancels the probes out.</summary>
    public void Tick(bool watched)
    {
        if (!watched)
        {
            CancellationTokenSource? stop = null;
            lock (_probes)
                // Not once disposed: the dispose cancels the source it took, which this would dispose under it.
                if (!_disposed && _probes.Values.Any(probe => !probe.IsCompleted))
                    (stop, _stop) = (_stop, new CancellationTokenSource());
            // Cancelled first, so a probe that links its token only now still sees it cancelled.
            stop?.Cancel();
            stop?.Dispose();
            return;
        }
        lock (_probes)
        {
            // Disposed: no probe it wouldn't wait for.
            if (++_ticks < Every || _disposed)
                return;
            _ticks = 0;
            foreach (var machine in _machines.Remotes)
            {
                if (_probes.TryGetValue(machine.Name!, out var probe) && !probe.IsCompleted)
                    continue;
                if (_probeStops.Remove(machine.Name!, out var old)) old.Dispose();
                var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                _probeStops[machine.Name!] = stop;
                var status = _status.ForProbe(machine.Name!);
                _probes[machine.Name!] = Task.Run(() => ProbeAsync(machine, status, stop.Token));
            }
        }
    }

    private async Task ProbeAsync(IMachine machine, MachineStatus status, CancellationToken stop)
    {
        try
        {
            using var limit = new CancellationTokenSource(ProbeLimit, _time);
            using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, limit.Token);
            await machine.ProbeAsync(either.Token);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            status.Unreachable(machine.Name!, "didn't answer");
        }
        catch (Exception)
        {
            // The machine recorded what it saw, and a probe cancelled for nobody watching records nothing.
        }
    }

    public async Task StopProbesAsync(string name)
    {
        Task? probe;
        CancellationTokenSource? stop;
        lock (_probes)
        {
            _machines.Remove(name);
            _probes.Remove(name, out probe);
            _probeStops.Remove(name, out stop);
        }
        if (stop is not null) await stop.CancelAsync();
        if (probe is not null) await probe;
        stop?.Dispose();
    }

    /// <summary>Cancels the probes out and waits for them to end.</summary>
    public async ValueTask DisposeAsync()
    {
        Task[] probes;
        CancellationTokenSource stop;
        lock (_probes)
        {
            _disposed = true;
            (probes, stop) = ([.. _probes.Values], _stop);
        }
        await stop.CancelAsync();
        await Task.WhenAll(probes);
        foreach (var source in _probeStops.Values) source.Dispose();
        stop.Dispose();
    }

    internal sealed record NameArgs(string Name);

    internal sealed record AgentsArgs(string? Machine = null);

    internal sealed record ConnectArgs(string Machine);

    internal sealed record Agent(string Id, string Name);

    internal sealed record AgentList(IReadOnlyList<Agent> Agents);
}
