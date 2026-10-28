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
    private readonly RemoteMachine? _remote;
    private readonly RemoteOutbox? _outbox;
    private readonly WebApplication _app;
    private readonly FileStream _homeLock;
    private readonly DiagnosticsHost _diagnostics;
    private readonly MachinesFeature _machinesFeature;

    private DesktopBackend(FileStream homeLock, AppDatabase db, LiveHub hub, SessionRegistry sessions, BackgroundWork work, RemoteMachine? remote,
        RemoteOutbox? outbox, WebApplication app, DiagnosticsHost diagnostics, MachinesFeature machinesFeature, BoardTools board, RemoteWorkflowMirror? boardMirror, Topics topics, Topics.Subscription? boardMachines, CancellationTokenSource hostStop)
    {
        _homeLock = homeLock;
        _board = board;
        _hostStop = hostStop;
        _boardMirror = boardMirror;
        _topics = topics;
        _boardMachines = boardMachines;
        _machinesFeature = machinesFeature;
        _remote = remote;
        _outbox = outbox;
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
    /// Starts it; with <paramref name="remote"/>, that machine's sessions are shown too and new ones can run there, and
    /// <paramref name="tunnel"/>'s exits mark it unreachable. With <paramref name="utilityClis"/>, those CLIs run the utility
    /// model that names new sessions.
    /// </summary>
    public static async Task<DesktopBackend> StartAsync(string home, string token, int port, IAgentLauncher launcher, TextWriter log, RemoteConfig? remote = null,
        RemoteTunnel? tunnel = null, (string Claude, string Codex)? utilityClis = null)
    {
        Directory.CreateDirectory(home);
        // Before the database opens, so a second backend on this home writes nothing into the live one's; released if
        // the start fails, so the home isn't left locked.
        var homeLock = HomeLock.Take(home);
        try
        {
            return await StartAsync(homeLock, home, token, port, launcher, log, remote, tunnel, utilityClis);
        }
        catch
        {
            homeLock.Dispose();
            throw;
        }
    }

    private static async Task<DesktopBackend> StartAsync(FileStream homeLock, string home, string token, int port, IAgentLauncher launcher, TextWriter log,
        RemoteConfig? remote, RemoteTunnel? tunnel, (string Claude, string Codex)? utilityClis)
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
        var status = new MachineStatus(topics, remote is null ? [] : [remote.Name]);
        var remoteMachine = remote is null ? null : new RemoteMachine(remote.Name, remote.Address, remote.Token, hub, sessionStore, log, status,
            sessions.Helpers.Row.TurnEnded);
        var machines = remoteMachine is null
            ? new MachineSet(new LocalMachine(hub, sessionStore, sessions, attachments))
            : new MachineSet(new LocalMachine(hub, sessionStore, sessions, attachments), remoteMachine);
        sessions.Helpers.Delivery.Machines = machines;
        var hostStop = new CancellationTokenSource();
        var board = new BoardTools(sessionStore, machines, boardTransitions, boardQueries, hostStop.Token);
        var boardMirror = remote is null ? null : new RemoteWorkflowMirror(remote, boardQueries, boardTransitions,
            () => status.Unreachable(remote.Name, "Board connection closed"));
        var workflowCalls = new WorkflowCalls(boardQueries, board.AnswerAsync, boardMirror);
        if (tunnel is not null && remote is not null)
            tunnel.Exited += () => status.Unreachable(remote.Name, "ssh tunnel exited");
        var outbox = remoteMachine is null ? null : new RemoteOutbox(remoteMachine, sessionStore, hub, machines.Named(null)!, log);
        // Turns a remote was running when the desktop stopped: catch up on them rather than mark them lost. And remote
        // helpers whose row here is still running, whose rows may not have been copied yet: once caught up with no turn
        // open, their row goes idle.
        remoteMachine?.WatchRunning(store.UnfinishedRuns().Select(run => run.SessionId).Where(id => sessionStore.Get(id)?.Machine == remote!.Name),
            store.RunningItems(RowKind.Subagent).Select(row => row.Payload.GetProperty("sessionId").GetString()!)
                .Where(id => sessionStore.Get(id)?.Machine == remote!.Name).ToList());

        Uri? address = null;
        Action dropSockets = () => { };
        var page = new PageRoutes(token, Path.Combine(home, "soak-results"), () => address!);
        var clock = new BackendClock(topics, TimeProvider.System, work);
        var machinesFeature = new MachinesFeature(machines, status, clock.Start, TimeProvider.System, () => tunnel?.TryNow());
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
        var app = ApiServer.Build(new ApiServices(hub, sessionStore, machines, attachments, token, sessions.Helpers, db.Writer, topics,
            [workflowCalls, new PreferencesFeature(db), clock, new RowsFeature(hub, store, sessionStore, machines), new SessionsFeature(sessionStore, machines),
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
        var backend = new DesktopBackend(homeLock, db, hub, sessions, work, remoteMachine, outbox, app, diagnostics, machinesFeature, board, boardMirror, topics, boardMachines, hostStop);
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
        await _machinesFeature.DisposeAsync();
        if (_outbox is not null)
            await _outbox.DisposeAsync();
        await _sessions.DisposeAsync();
        // After the sessions, so a stop's cancel fails fast; before the database, which a write still finishing needs.
        await _work.DisposeAsync();
        if (_remote is not null)
            await _remote.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
        _homeLock.Dispose();
    }
}
