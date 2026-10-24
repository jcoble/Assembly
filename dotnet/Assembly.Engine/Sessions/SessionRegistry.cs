using System.Collections.Concurrent;
using Assembly.Engine.Agents;
using Assembly.Engine.Helpers;
using Assembly.Engine.Live;
using Assembly.Engine.Threads;

namespace Assembly.Engine.Sessions;

/// <summary>
/// The session host of each thread that has been sent to, and each agent's one shared adapter. A host holds no
/// agent between turns: each turn opens its session on its agent's shared adapter and closes it at the end.
/// </summary>
public sealed class SessionRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionHost> _hosts = new();
    private readonly LiveHub _hub;
    private readonly ThreadStore _threads;
    private readonly ConcurrentDictionary<string, SharedAdapter> _adapters = new();
    private readonly IAgentLauncher _launcher;
    private readonly SessionOpener _opener;
    private readonly TimeProvider _time;
    private readonly TextWriter _log;
    // Each helper session's thread, by id, with the thread whose agent started it.
    private readonly ConcurrentDictionary<string, string> _helperOf = new();
    // The last run a helper's prompt or message went into, before its rows reach here from another machine.
    private readonly ConcurrentDictionary<string, string> _helperRun = new();

    public SessionRegistry(LiveHub hub, ThreadStore threads, IAgentLauncher launcher, TimeProvider time, TextWriter log,
        HelperLimits? limits = null)
    {
        _hub = hub;
        _threads = threads;
        _launcher = launcher;
        Helpers = new HelperSessions(threads, this, hub, limits ?? new HelperLimits());
        _opener = new SessionOpener(threads, launcher, agent => _adapters.GetOrAdd(agent, id => new SharedAdapter(launcher, id, time)),
            Helpers.Tokens.ServersFor);
        _time = time;
        _log = log;
    }

    /// <summary>The agents this machine can run.</summary>
    public IReadOnlyList<AgentDescriptor> Agents => _launcher.Agents;

    /// <summary>The Assembly MCP server's side: helper sessions, and the messages between sessions.</summary>
    public HelperSessions Helpers { get; }

    public SessionHost For(string threadId) =>
        _hosts.GetOrAdd(threadId, id =>
        {
            if (_threads.Get(id) is { Helper: true, ParentThreadId: { } parent })
                _helperOf[id] = parent;
            return new SessionHost(id, _hub, _threads, _opener, _time, _log, () => HelpersWorking(id), Helpers.Row.TurnEnded, Helpers.Tokens.Revoke);
        });

    /// <summary>Whether a helper session the thread's agent started has a turn running.</summary>
    public bool HelpersWorking(string threadId) =>
        _helperOf.Any(pair => pair.Value == threadId && Working(pair.Key));

    /// <summary>
    /// A helper the thread's agent started, here or on another machine, and the run its prompt or a message went
    /// into: the thread stays open while it works.
    /// </summary>
    public void TrackHelper(string helperId, string parentId, string runId)
    {
        _helperOf[helperId] = parentId;
        _helperRun[helperId] = runId;
    }

    /// <summary>
    /// Whether the thread has a turn running: here, in the rows copied from its machine, or a helper's run that was
    /// sent there and hasn't ended in the copied rows yet.
    /// </summary>
    public bool Working(string threadId) =>
        ActiveRun(threadId) is not null || _hub.OpenRun(threadId) is not null
        || (_helperRun.TryGetValue(threadId, out var run) && !_hub.RunEnded(threadId, run));

    /// <summary>Whether the thread's session is open here now.</summary>
    public bool IsOpen(string threadId) => _hosts.TryGetValue(threadId, out var host) && host.IsOpen;

    /// <summary>The threads whose sessions are open here now.</summary>
    public IEnumerable<string> OpenThreads => _hosts.Where(pair => pair.Value.IsOpen).Select(pair => pair.Key);

    /// <summary>The thread's running turn, or null when it's idle.</summary>
    public string? ActiveRun(string threadId) => _hosts.TryGetValue(threadId, out var host) ? host.ActiveRunId : null;

    /// <summary>Stops the run wherever it's running. False if no thread is running it.</summary>
    public bool Stop(string runId) => _hosts.Values.Any(host => host.Stop(runId));

    /// <summary>Stops every running turn and the adapter. A turn cut short here ends as rows like any other.</summary>
    public async ValueTask DisposeAsync()
    {
        // Sessions kept for background work stop it and close cleanly while their adapter is still there.
        try
        {
            await Task.WhenAll(_hosts.Values.Select(host => host.CloseKeptForShutdownAsync())).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _log.WriteLine("shutdown: a kept session didn't close in 5 s; its adapter goes anyway");
        }

        var turns = _hosts.Values.Select(host => host.StopForShutdownAsync()).ToList();
        foreach (var adapter in _adapters.Values)
            await adapter.DisposeAsync();
        await Task.WhenAll(turns);
    }
}
