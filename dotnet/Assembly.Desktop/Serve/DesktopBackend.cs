using System.Text.Json;
using Assembly.Application.Features.Workflows;
using Assembly.Infrastructure.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Mcp;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Infrastructure.Chat.Worktrees;
using Assembly.Infrastructure.Chat.SourceControl;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Domain.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Diagnostics;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Topics;
using Assembly.Application.Features.Preferences;
using Assembly.Application.Features.Diagnostics;
using Assembly.Infrastructure.Clock;
using Assembly.Application.Features.Terminal;
using Assembly.Infrastructure.Terminal;

namespace Assembly.Desktop.Serve;

/// <summary>
/// The desktop backend, running: the row store and sessions list in <c>home/assembly.db</c>, the live hub, the local
/// sessions and the API. Port 0 picks a free port (for tests).
/// </summary>
public sealed class DesktopBackend : IAsyncDisposable
{
    private readonly AppDatabase _db;
    private readonly BoardTools _board;
    private readonly CancellationTokenSource _hostStop;
    private readonly RemoteWorkflowMirror? _boardMirror;
    private readonly Topics _topics;
    private readonly Topics.Subscription? _boardMachines;
    private readonly LiveHub _hub;
    private readonly SessionRegistry _sessions;
    private readonly BackgroundWork _work;
    private readonly RemoteConnections _connections;
    private readonly WebApplication _app;
    private readonly FileStream _homeLock;
    private readonly DiagnosticsHost _diagnostics;
    private readonly MachinesFeature _machinesFeature;
    private readonly TerminalHost _terminals;

    private DesktopBackend(FileStream homeLock, AppDatabase db, LiveHub hub, SessionRegistry sessions, BackgroundWork work, RemoteConnections connections,
        WebApplication app, DiagnosticsHost diagnostics, MachinesFeature machinesFeature, TerminalHost terminals, BoardTools board, RemoteWorkflowMirror? boardMirror, Topics topics, Topics.Subscription? boardMachines, CancellationTokenSource hostStop)
    {
        _homeLock = homeLock;
        _terminals = terminals;
        _board = board;
        _hostStop = hostStop;
        _boardMirror = boardMirror;
        _topics = topics;
        _boardMachines = boardMachines;
        _machinesFeature = machinesFeature;
        _connections = connections;
        _db = db;
        _hub = hub;
        _sessions = sessions;
        _work = work;
        _app = app;
        _diagnostics = diagnostics;
        Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    /// <summary>Where the API listens, as <c>http://127.0.0.1:port</c>.</summary>
    public Uri Address { get; }

    /// <summary>Whether App Nap is held, while any turn runs.</summary>
    internal bool ActivityHeld => _diagnostics.ActivityHeld;

    /// <summary>How many of its background works are still running.</summary>
    internal int BackgroundRunning => _work.Running;

    /// <summary>
    /// Starts local serving independently of saved connection attempts. A supplied remote is a direct test endpoint;
    /// production startup imports SSH settings with savedRemote. Utility CLIs name new sessions.
    /// </summary>
    public static async Task<DesktopBackend> StartAsync(string home, string token, int port, IAgentLauncher launcher, TextWriter log, RemoteConfig? remote = null,
        RemoteTunnel? tunnel = null, (string Claude, string Codex)? utilityClis = null, SavedMachine? savedRemote = null)
    {
        Directory.CreateDirectory(home);
        // Before the database opens, so a second backend on this home writes nothing into the live one's; released if
        // the start fails, so the home isn't left locked.
        var homeLock = HomeLock.Take(home);
        try
        {
            return await StartAsync(homeLock, home, token, port, launcher, log, remote, tunnel, utilityClis, savedRemote);
        }
        catch
        {
            homeLock.Dispose();
            throw;
        }
    }

    private static async Task<DesktopBackend> StartAsync(FileStream homeLock, string home, string token, int port, IAgentLauncher launcher, TextWriter log,
        RemoteConfig? remote, RemoteTunnel? tunnel, (string Claude, string Codex)? utilityClis, SavedMachine? savedRemote)
    {
        var database = Path.Combine(home, "assembly.db");
        var topics = new Topics();
        var db = AppDatabase.Open(database, TimeProvider.System, topics);
        WorkflowSchema.Open(db);
        var boardQueries = new WorkflowQueries(db, remote?.Name);
        var boardTransitions = new WorkflowTransitions(db, TimeProvider.System);
        boardTransitions.Recover();
        // After the database opens, so a database the rename can't copy first stops with the checkouts left alone, and
        // before the API starts, so no session makes a checkout in sessions/ first.
        var checkouts = Path.Combine(home, "sessions");
        AdapterLauncher.MoveOldCheckouts(home, checkouts, log);
        var store = RowStore.Open(db);
        // Filled before recovery, whose closing rows go through the hub and so update it.
        var summaries = SessionSummaries.Load(store);
        var sessionStore = SessionStore.Open(db, TimeProvider.System, checkouts, summaries);
        // App Nap is held while any session has a run open, here or on a remote.
        var diagnostics = new DiagnosticsHost(Path.Combine(home, "soak-results"));
        var hub = new LiveHub(store, topics: topics, summaries: summaries, summaryChanged: session =>
        {
            sessionStore.PublishSummary(session);
            diagnostics.HoldActivity(summaries.AnyRunOpen);
        });
        var closed = RunRecovery.CloseUnfinished(store, hub, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            runsHere: session => sessionStore.Get(session) is not { Machine: not null });
        if (closed > 0)
            log.WriteLine($"closed {closed} turn(s) the last run left unfinished");
        // Loading the summaries tells nobody: a remote's run left open holds it from the start.
        diagnostics.HoldActivity(summaries.AnyRunOpen);
        var work = new BackgroundWork(TimeProvider.System, log);
        var sessions = new SessionRegistry(hub, sessionStore, launcher, TimeProvider.System, log, work, titler: utilityClis is { } clis
            ? new SessionTitler(sessionStore, store, new CliUtilityModel(clis.Claude, clis.Codex, new PreferenceStore(db), TimeProvider.System, log))
            : null);
        var attachments = new AttachmentStore(Path.Combine(home, "attachments"));
        var profiles = new MachineStore(db);
        var status = new MachineStatus(topics, []);
        var machines = new MachineSet(new LocalMachine(hub, sessionStore, sessions, attachments));
        sessions.Helpers.Delivery.Machines = machines;


        Uri? address = null;
        Action dropSockets = () => { };
        var page = new PageRoutes(token, Path.Combine(home, "soak-results"), () => address!);
        var clock = new BackendClock(topics, TimeProvider.System, work);
        RemoteConnections? connections = null;
        var machinesFeature = new MachinesFeature(machines, status, clock.Start, TimeProvider.System, store: profiles,
            connection: (name, connect, cancel) => connections!.ConnectionAsync(name, connect, cancel));
        connections = new RemoteConnections(profiles, machines, status, hub, sessionStore, topics, log,
            machinesFeature, sessions.Helpers.Row.TurnEnded,
            open: remote is null ? null : (profile, cancel) =>
            {
                cancel.ThrowIfCancellationRequested();
                return Task.FromResult<(RemoteConfig, IAsyncDisposable?)>((remote, tunnel));
            });
        connections.Start(savedRemote ?? (remote is null ? null : new SavedMachine(remote.Name, remote.Name)));
        // Direct test endpoints use the same owner; their callers historically expect routing ready on return.
        if (remote is not null) await connections.ReadyAsync(remote.Name);
        var startup = connections.StartupSnapshot(savedRemote?.Name ?? remote?.Name);
        var hostStop = new CancellationTokenSource();
        var board = new BoardTools(sessionStore, sessions, boardTransitions, boardQueries, hostStop.Token);
        var boardMirror = startup is { } connected ? new RemoteWorkflowMirror(connected.Config, boardQueries, boardTransitions,
            connected.Unreachable) : null;
        var workflowCalls = new WorkflowCalls(boardQueries, board.AnswerAsync, boardMirror);
        clock.Ticked += machinesFeature.Tick;
        var boardMachines = boardMirror is null ? null : topics.Subscribe("machines", null, _ =>
        {
            foreach (var item in status.Items)
                if (item.Name == boardMirror.Name && item.Reachable) boardMirror.Reconnect();
            clock.Start();
        }, (_, json) =>
        {
            var item = JsonSerializer.Deserialize(json, SessionsJson.Default.MachineItem)!;
            if (item.Name == boardMirror.Name && item.Reachable) boardMirror.Reconnect();
        });
        boardMirror?.Reconnect();
        var terminals = new TerminalHost(new UnixPtyStarter(), topics, TimeProvider.System, AdapterLauncher.ExistingFolder(checkouts, sessionStore));
        var app = ApiServer.Build(new ApiServices(hub, sessionStore, machines, attachments, token, sessions.Helpers, db.Writer, topics,
            [new TerminalFeature(terminals), workflowCalls, new PreferencesFeature(db), clock, new RowsFeature(hub, store, sessionStore, machines), new SessionsFeature(sessionStore, machines),
                new SendFeature(sessionStore, machines, hub), new ConfigFeature(sessionStore, machines), machinesFeature,
                new AttachmentsFeature(sessionStore, machines, attachments, Path.Combine(home, "staging", "attachments")),
                new FilesFeature(sessionStore),
                new WorktreesFeature(sessionStore),
                new SourceControlFeature(sessionStore),
                new DiagnosticsFeature(diagnostics, () =>
                {
                    hub.CloseSubscriptions();
                    dropSockets();
                })], board), port, out dropSockets, routes =>
        {
            MachineFeatureList.Add(routes, new FeatureContext(db));
            DesktopFeatureList.Add(routes, new FeatureContext(db));
            new DebugRoutes(hub).Map(routes);
            page.Map(routes);
        });
        app.Lifetime.ApplicationStopping.Register(hostStop.Cancel);
        await app.StartAsync();
        var backend = new DesktopBackend(homeLock, db, hub, sessions, work, connections, app, diagnostics, machinesFeature, terminals, board, boardMirror, topics, boardMachines, hostStop);
        address = backend.Address;
        // Sessions here join this machine's MCP server, on the API's port.
        sessions.Helpers.Tokens.Url = new Uri(backend.Address, "/mcp").ToString();
        return backend;
    }

    public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    /// <summary>
    /// Stops it, as SIGTERM does, once <paramref name="input"/> ends: the window's launcher holds this process's stdin
    /// open for its whole life, so its quit or crash ends this backend too. The dispose cancels the read.
    /// </summary>
    public void StopAtEndOf(Stream input) => _work.Start("stdin", async stop =>
    {
        var buffer = new byte[256];
        // WaitAsync, as a console stream's read doesn't see the token.
        while (await input.ReadAsync(buffer, stop).AsTask().WaitAsync(stop) > 0)
        {
        }
        _app.Lifetime.StopApplication();
    });

    public async ValueTask DisposeAsync()
    {
        await _hostStop.CancelAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (_boardMachines is not null) _topics.Unsubscribe(_boardMachines);
        if (_boardMirror is not null) await _boardMirror.DisposeAsync();
        await _board.DisposeAsync();
        _hostStop.Dispose();
        // Before the remote, so no probe is still out on it.
        await _connections.DisposeAsync();
        await _machinesFeature.DisposeAsync();
        await _terminals.DisposeAsync();
        await _sessions.DisposeAsync();
        // After the sessions, so a stop's cancel fails fast; before the database, which a write still finishing needs.
        await _work.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
        _homeLock.Dispose();
    }
}
