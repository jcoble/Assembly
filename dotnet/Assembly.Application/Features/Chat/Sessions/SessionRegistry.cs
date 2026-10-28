using System.Collections.Concurrent;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Helpers;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// The session host of each session that has been sent to, and each agent's one shared adapter. A host holds no
/// agent between turns: each turn opens its session on its agent's shared adapter and closes it at the end.
/// </summary>
public sealed class SessionRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionHost> _hosts = new();
    private readonly LiveHub _hub;
    private readonly SessionStore _sessionStore;
    private readonly ConcurrentDictionary<string, SharedAdapter> _adapters = new();
    private readonly IAgentLauncher _launcher;
    private readonly SessionOpener _opener;
    private readonly TimeProvider _time;
    private readonly TextWriter _log;
    private readonly BackgroundWork _work;
    private readonly SessionTitler? _titler;
    // Each session another session's agent started, by id, with the session whose agent started it.
    private readonly ConcurrentDictionary<string, string> _startedBy = new();
    // The last run a helper's prompt or message went into, before its rows reach here from another machine.
    private readonly ConcurrentDictionary<string, string> _helperRun = new();

    public SessionRegistry(LiveHub hub, SessionStore sessionStore, IAgentLauncher launcher, TimeProvider time, TextWriter log,
        BackgroundWork work, HelperLimits? limits = null, SessionTitler? titler = null)
    {
        _hub = hub;
        _sessionStore = sessionStore;
        _launcher = launcher;
        Helpers = new HelperSessions(sessionStore, this, hub, limits ?? new HelperLimits());
        _opener = new SessionOpener(sessionStore, launcher, agent => _adapters.GetOrAdd(agent, id => new SharedAdapter(launcher, id, time, log)),
            Helpers.Tokens.ServersFor);
        _time = time;
        _log = log;
        _work = work;
        _titler = titler;
    }

    /// <summary>The agents this machine can run.</summary>
    public IReadOnlyList<AgentDescriptor> Agents => _launcher.Agents;

    /// <summary>The Assembly MCP server's side: helper sessions, and the messages between sessions.</summary>
    public HelperSessions Helpers { get; }

    public SessionHost For(string sessionId) =>
        _hosts.GetOrAdd(sessionId, id =>
        {
            if (_sessionStore.Get(id) is { Helper: true, ParentSessionId: { } parent })
                _startedBy[id] = parent;
            return new SessionHost(id, _hub, _sessionStore, _opener, _time, _log, _work, () => StartedWorking(id), Helpers.Row.TurnEnded, Helpers.Tokens.Revoke,
                _titler);
        });

    /// <summary>Whether a session the session's agent started has a turn running.</summary>
    public bool StartedWorking(string sessionId) =>
        _startedBy.Any(pair => pair.Value == sessionId && Working(pair.Key));

    /// <summary>
    /// A helper the session's agent started, here or on another machine, and the run its prompt or a message went
    /// into: the session stays open while it works.
    /// </summary>
    public void TrackHelper(string helperId, string parentId, string runId)
    {
        _startedBy[helperId] = parentId;
        _helperRun[helperId] = runId;
    }

    /// <summary>
    /// Whether the session has a turn running: here, in the rows copied from its machine, or a helper's run that was
    /// sent there and hasn't ended in the copied rows yet.
    /// </summary>
    public bool Working(string sessionId) =>
        ActiveRun(sessionId) is not null || _hub.OpenRun(sessionId) is not null
        || (_helperRun.TryGetValue(sessionId, out var run) && !_hub.RunEnded(sessionId, run));

    /// <summary>Whether the session's ACP session is open here now.</summary>
    public bool IsOpen(string sessionId) => _hosts.TryGetValue(sessionId, out var host) && host.IsOpen;

    /// <summary>The sessions whose ACP sessions are open here now.</summary>
    public IEnumerable<string> OpenSessions => _hosts.Where(pair => pair.Value.IsOpen).Select(pair => pair.Key);

    /// <summary>The session's running turn, or null when it's idle.</summary>
    public string? ActiveRun(string sessionId) => _hosts.TryGetValue(sessionId, out var host) ? host.ActiveRunId : null;

    /// <summary>Stops the run wherever it's running. False if no session is running it.</summary>
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
