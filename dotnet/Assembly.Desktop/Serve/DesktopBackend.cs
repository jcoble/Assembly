using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Mcp;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
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
    private readonly LiveHub _hub;
    private readonly SessionRegistry _sessions;
    private readonly RemoteMachine? _remote;
    private readonly RemoteOutbox? _outbox;
    private readonly WebApplication _app;
    private readonly DiagnosticsHost _diagnostics;
    private readonly MachinesFeature _machinesFeature;

    private DesktopBackend(AppDatabase db, LiveHub hub, SessionRegistry sessions, RemoteMachine? remote, RemoteOutbox? outbox,
        WebApplication app, DiagnosticsHost diagnostics, MachinesFeature machinesFeature)
    {
        _machinesFeature = machinesFeature;
        _remote = remote;
        _outbox = outbox;
        _db = db;
        _hub = hub;
        _sessions = sessions;
        _app = app;
        _diagnostics = diagnostics;
        Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    /// <summary>Where the API listens, as <c>http://127.0.0.1:port</c>.</summary>
    public Uri Address { get; }

    /// <summary>Whether App Nap is held, while any turn runs.</summary>
    internal bool ActivityHeld => _diagnostics.ActivityHeld;

    /// <summary>
    /// Starts it; with <paramref name="remote"/>, that machine's sessions are shown too and new ones can run there, and
    /// <paramref name="tunnel"/>'s exits mark it unreachable.
    /// </summary>
    public static async Task<DesktopBackend> StartAsync(string home, string token, int port, IAgentLauncher launcher, TextWriter log, RemoteConfig? remote = null,
        RemoteTunnel? tunnel = null)
    {
        Directory.CreateDirectory(home);
        var database = Path.Combine(home, "assembly.db");
        var topics = new Topics();
        var db = AppDatabase.Open(database, TimeProvider.System, topics);
        // After the database opens, so a database the rename can't copy first stops with the checkouts left alone, and
        // before the API starts, so no session makes a checkout in sessions/ first.
        AdapterLauncher.MoveOldCheckouts(home, Path.Combine(home, "sessions"), log);
        var store = RowStore.Open(db);
        // Filled before recovery, whose closing rows go through the hub and so update it.
        var summaries = SessionSummaries.Load(store);
        var sessionStore = SessionStore.Open(db, TimeProvider.System, summaries);
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
        var sessions = new SessionRegistry(hub, sessionStore, launcher, TimeProvider.System, log);
        var attachments = new AttachmentStore(Path.Combine(home, "attachments"));
        var status = new MachineStatus(topics, remote is null ? [] : [remote.Name]);
        var remoteMachine = remote is null ? null : new RemoteMachine(remote.Name, remote.Address, remote.Token, hub, sessionStore, log, status,
            sessions.Helpers.Row.TurnEnded);
        var machines = remoteMachine is null
            ? new MachineSet(new LocalMachine(hub, sessionStore, sessions, attachments))
            : new MachineSet(new LocalMachine(hub, sessionStore, sessions, attachments), remoteMachine);
        sessions.Helpers.Delivery.Machines = machines;
        if (tunnel is not null && remote is not null)
            tunnel.Exited += () => status.Unreachable(remote.Name, "ssh tunnel exited");
        var outbox = remoteMachine is null ? null : new RemoteOutbox(remoteMachine, sessionStore, hub, machines.Named(null)!, log);
        // Turns a remote was running when the desktop stopped: catch up on them rather than mark them lost.
        remoteMachine?.WatchRunning(store.UnfinishedRuns().Select(run => run.SessionId).Where(id => sessionStore.Get(id)?.Machine == remote!.Name));

        Uri? address = null;
        Action dropSockets = () => { };
        var page = new PageRoutes(token, Path.Combine(home, "soak-results"), () => address!);
        // The clock isn't disposed: a closing socket's finally unsubscribes everything, so it stops within a second of
        // the last socket.
        var clock = new BackendClock(topics, TimeProvider.System);
        var machinesFeature = new MachinesFeature(machines, status, clock.Start, TimeProvider.System);
        clock.Ticked += machinesFeature.Tick;
        var app = ApiServer.Build(new ApiServices(hub, sessionStore, machines, attachments, token, sessions.Helpers, db.Writer, topics,
            [new PreferencesFeature(db), clock, new RowsFeature(hub, store, sessionStore, machines), new SessionsFeature(sessionStore, machines),
                new SendFeature(sessionStore, machines, hub), new ConfigFeature(sessionStore, machines), machinesFeature,
                new AttachmentsFeature(sessionStore, machines, attachments, Path.Combine(home, "staging", "attachments")),
                new DiagnosticsFeature(diagnostics, () =>
                {
                    hub.CloseSubscriptions();
                    dropSockets();
                })]), port, out dropSockets, routes =>
        {
            MachineFeatureList.Add(routes, new FeatureContext(db));
            DesktopFeatureList.Add(routes, new FeatureContext(db));
            new DebugRoutes(hub).Map(routes);
            page.Map(routes);
        });
        await app.StartAsync();
        var backend = new DesktopBackend(db, hub, sessions, remoteMachine, outbox, app, diagnostics, machinesFeature);
        address = backend.Address;
        // Sessions here join this machine's MCP server, on the API's port.
        sessions.Helpers.Tokens.Url = new Uri(backend.Address, "/mcp").ToString();
        return backend;
    }

    public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        // Before the remote, so no probe is still out on it.
        await _machinesFeature.DisposeAsync();
        if (_outbox is not null)
            await _outbox.DisposeAsync();
        await _sessions.DisposeAsync();
        if (_remote is not null)
            await _remote.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
    }
}
