using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// The machines a new session can run on, over the socket (plan C1): the <c>machines</c> topic sends each one and
/// whether it answered last time, as <c>machines/list</c> answers it once, and <c>machines/agents</c> answers the agents a machine (this one when left out) can
/// run. While the topic has a subscriber, the backend clock's ticks probe each remote: on the first tick after a
/// subscribe, then every 15th.
/// </summary>
internal sealed class MachinesFeature : IFeature, IAsyncDisposable
{
    public const string Feature = "machines";
    public const string Topic = "machines";

    private const int Every = 15;
    private static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(5);

    private readonly MachineSet _machines;
    private readonly MachineStatus _status;
    private readonly Action _startClock;
    private readonly TimeProvider _time;
    // Each remote's last probe, run off the tick's thread; a remote whose probe is still out gets no second. Guarded
    // by itself, as are _stop, _ticks and _disposed.
    private readonly Dictionary<string, Task> _probes = [];
    private CancellationTokenSource _stop = new();
    private int _ticks = Every - 1;
    private bool _disposed;

    /// <param name="startClock">Starts the backend clock, whose ticks come back through <see cref="Tick"/>.</param>
    public MachinesFeature(MachineSet machines, MachineStatus status, Action startClock, TimeProvider time)
    {
        _machines = machines;
        _status = status;
        _startClock = startClock;
        _time = time;
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
            return _status.Items;
        });
        calls.Read(Feature, "list", SessionsJson.Default.ListArgs, SessionsJson.Default.MachineList, (_, _) =>
            Task.FromResult(new MachineList(_status.Items)));
        calls.Read(Feature, "agents", SessionsJson.Default.AgentsArgs, SessionsJson.Default.AgentList, async (args, call) =>
        {
            // An empty name is this machine, as GET /agents?machine= takes it.
            var machine = _machines.Named(args.Machine is { Length: > 0 } name ? name : null) ?? throw new CallFailed("unknown machine");
            return new AgentList([.. (await machine.AgentsAsync(call.Cancellation)).Select(agent => new Agent(agent.Id, agent.Name))]);
        });
    }

    /// <summary>Each second of the backend clock, with whether <c>machines</c> has a subscriber. Nobody left watching cancels the probes out.</summary>
    public void Tick(bool watched)
    {
        if (!watched)
        {
            CancellationTokenSource? stop = null;
            lock (_probes)
                if (_probes.Values.Any(probe => !probe.IsCompleted))
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
                var stop = _stop.Token;
                _probes[machine.Name!] = Task.Run(() => ProbeAsync(machine, stop));
            }
        }
    }

    private async Task ProbeAsync(IMachine machine, CancellationToken stop)
    {
        try
        {
            using var limit = new CancellationTokenSource(ProbeLimit, _time);
            using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, limit.Token);
            await machine.ProbeAsync(either.Token);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            _status.Unreachable(machine.Name!, "didn't answer");
        }
        catch (Exception)
        {
            // The machine recorded what it saw, and a probe cancelled for nobody watching records nothing.
        }
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
    }

    internal sealed record ListArgs;

    internal sealed record MachineList(IReadOnlyList<MachineItem> Machines);

    internal sealed record AgentsArgs(string? Machine = null);

    internal sealed record Agent(string Id, string Name);

    internal sealed record AgentList(IReadOnlyList<Agent> Agents);
}
