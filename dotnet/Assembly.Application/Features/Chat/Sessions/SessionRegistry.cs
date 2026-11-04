using Assembly.Application.Features.Chat.Sessions.Mcp;
using System.Collections.Concurrent;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Helpers;
using Assembly.Application.Features.Chat.Sessions.Configuration;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// The session host of each session that has been sent to, and each agent's one shared adapter. A host holds no
/// agent between turns: each turn opens its session on its agent's shared adapter and closes it at the end.
/// </summary>
public sealed partial class SessionRegistry : IAsyncDisposable, Assembly.Application.Common.Engine.ISessionMaintenance
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
    private readonly ISessionFeed? _feed;
    private readonly SessionMcp _mcp;
    public Assembly.Application.Common.Engine.INativeSessionResolver? NativeSessions { get; private set; }
    public void BindNativeSessions(Assembly.Application.Common.Engine.INativeSessionResolver resolver)
    {
        if (NativeSessions is not null) throw new InvalidOperationException("Native sessions are already bound.");
        NativeSessions = resolver;
        _opener.Native.Resolver = resolver;
    }
    private sealed record HelperProjection(Func<string, bool> Working, Action<string> TurnEnded);
    private HelperProjection? _helperProjection;

    public SessionRegistry(LiveHub hub, SessionStore sessionStore, IAgentLauncher launcher, TimeProvider time, TextWriter log,
        BackgroundWork work, SessionTitler? titler = null, ISessionFeed? feed = null, SessionMcp? mcp = null)
    {
        _feed = feed;
        _hub = hub;
        _sessionStore = sessionStore;
        _launcher = launcher;
        Helpers = new HelperSessions(sessionStore, this, hub);
        _mcp = mcp ?? new SessionMcp(_ => Task.CompletedTask, (_, _) => [], _ => { }, (_, _) => { });
        _opener = new SessionOpener(sessionStore, launcher, Adapter,
            _mcp);
        _time = time;
        _log = log;
        _work = work;
        _titler = titler;
    }

    /// <summary>The agents this machine can run.</summary>
    public IReadOnlyList<AgentDescriptor> Agents => _launcher.Agents;

    /// <summary>Safe configured targets, with both identities derived from one frozen launch environment.</summary>
    public IReadOnlyList<ConfigTarget> ConfigTargets => _launcher.Agents.Select(agent =>
    {
        var captured = ConfigTargetIdentity.Capture(_launcher, agent.Id);
        var account = AccountContext.Of(captured.Descriptor, key => captured.Environment.GetValueOrDefault(key));
        return new ConfigTarget(captured.Descriptor.Id, captured.Descriptor.Name, captured.TargetId,
            account, captured.Descriptor.Id + "/" + account);
    }).ToList();

    /// <summary>One bounded private metadata observation through the configured launcher.</summary>
    public Task<ComposerCatalogue> DiscoverConfigAsync(string agent, string targetId, string cwd, CancellationToken ct) =>
        new ComposerConfigDiscovery(_launcher, _time).DiscoverAsync(agent, targetId, cwd, ct);

    /// <summary>The existing parent-card writer used by the Engine helper projection.</summary>
    public HelperSessions Helpers { get; }

    public SessionHost For(string sessionId) =>
        _hosts.GetOrAdd(sessionId, id =>
        {
            return new SessionHost(id, _hub, _sessionStore, _opener, _time, _log, _work, () => StartedWorking(id), HelperTurnEnded, _mcp.Revoke,
                _titler, _feed);
        });

    /// <summary>Whether a session the session's agent started has a turn running.</summary>
    public bool StartedWorking(string sessionId) =>
        Volatile.Read(ref _helperProjection)?.Working(sessionId) == true;

    /// <summary>Use the committed Engine child projection when this registry runs under Engine ownership.</summary>
    public void BindHelperWorking(Func<string, bool> working, Action<string> turnEnded)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(turnEnded);
        if (Interlocked.CompareExchange(ref _helperProjection, new(working, turnEnded), null) is not null)
            throw new InvalidOperationException("Helper working projection is already bound.");
    }

    private void HelperTurnEnded(string sessionId)
    {
        Volatile.Read(ref _helperProjection)?.TurnEnded(sessionId);
    }

    /// <summary>
    /// Whether the session has a turn running here or in the rows copied from its machine.
    /// </summary>
    public bool Working(string sessionId) =>
        ActiveRun(sessionId) is not null || _hub.OpenRun(sessionId) is not null;

    /// <summary>Whether the session's ACP session is open here now.</summary>
    public bool IsOpen(string sessionId) => _hosts.TryGetValue(sessionId, out var host) && host.IsOpen;

    /// <summary>The sessions whose ACP sessions are open here now.</summary>
    public IEnumerable<string> OpenSessions => _hosts.Where(pair => pair.Value.IsOpen).Select(pair => pair.Key);

    public Assembly.Application.Common.Engine.SessionMaintenanceAttempt TryAcquire(string sessionId)
    {
        var info = _sessionStore.Get(sessionId);
        if (info is null || info.Machine is not null) return new(null, "unknown_session");
        return For(sessionId).TryAcquireMaintenance();
    }

    /// <summary>The session's running turn, or null when it's idle.</summary>
    public string? ActiveRun(string sessionId) => _hosts.TryGetValue(sessionId, out var host) ? host.ActiveRunId : null;

    /// <summary>Stops the run wherever it's running. False if no session is running it.</summary>
    public bool Stop(string runId) => _hosts.Values.Any(host => host.Stop(runId));

    /// <summary>Stops every running turn and the adapter. A turn cut short here ends as rows like any other.</summary>
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_hosts.Values.Select(host => host.StopWarmForShutdownAsync()));
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
