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
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;
using Assembly.Application.Features.Diagnostics;
using Assembly.Infrastructure.Diagnostics;
using Assembly.Infrastructure.Remote;

namespace Assembly.Mac.Serve;

/// <summary>
/// The Mac's backend, running: the row store and threads list in <c>home/assembly.db</c>, the live hub, the local
/// sessions and the API. Port 0 picks a free port (for tests).
/// </summary>
public sealed class MacBackend : IAsyncDisposable
{
    private readonly AppDatabase _db;
    private readonly LiveHub _hub;
    private readonly SessionRegistry _sessions;
    private readonly RemoteMachine? _remote;
    private readonly RemoteOutbox? _outbox;
    private readonly WebApplication _app;

    private MacBackend(AppDatabase db, LiveHub hub, SessionRegistry sessions, RemoteMachine? remote, RemoteOutbox? outbox,
        WebApplication app)
    {
        _remote = remote;
        _outbox = outbox;
        _db = db;
        _hub = hub;
        _sessions = sessions;
        _app = app;
        Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    /// <summary>Where the API listens, as <c>http://127.0.0.1:port</c>.</summary>
    public Uri Address { get; }

    /// <summary>Starts it; with <paramref name="remote"/>, that machine's threads are shown too and new ones can run there.</summary>
    public static async Task<MacBackend> StartAsync(string home, string token, int port, IAgentLauncher launcher, TextWriter log, RemoteConfig? remote = null)
    {
        Directory.CreateDirectory(home);
        var database = Path.Combine(home, "assembly.db");
        var db = AppDatabase.Open(database);
        var store = RowStore.Open(db);
        var threads = ThreadStore.Open(db, TimeProvider.System);
        var hub = new LiveHub(store);
        var closed = RunRecovery.CloseUnfinished(store, hub, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            runsHere: thread => threads.Get(thread) is not { Machine: not null });
        if (closed > 0)
            log.WriteLine($"closed {closed} turn(s) the last run left unfinished");
        var sessions = new SessionRegistry(hub, threads, launcher, TimeProvider.System, log);
        var attachments = new AttachmentStore(Path.Combine(home, "attachments"));
        var remoteMachine = remote is null ? null : new RemoteMachine(remote.Name, remote.Address, remote.Token, hub, threads, log,
            sessions.Helpers.Row.TurnEnded);
        var machines = remoteMachine is null
            ? new MachineSet(new LocalMachine(hub, threads, sessions, attachments))
            : new MachineSet(new LocalMachine(hub, threads, sessions, attachments), remoteMachine);
        sessions.Helpers.Delivery.Machines = machines;
        var outbox = remoteMachine is null ? null : new RemoteOutbox(remoteMachine, threads, hub, machines.Named(null)!, log);
        // Turns a remote was running when the Mac stopped: catch up on them rather than mark them lost.
        remoteMachine?.WatchRunning(store.UnfinishedRuns().Select(run => run.ThreadId).Where(id => threads.Get(id)?.Machine == remote!.Name));

        Uri? address = null;
        var page = new PageRoutes(token, Path.Combine(home, "soak-results"), () => address!);
        var app = ApiServer.Build(new ApiServices(hub, threads, machines, attachments, token, sessions.Helpers), port, routes =>
        {
            MachineFeatureList.Add(routes, new FeatureContext(db));
            MacFeatureList.Add(routes, new FeatureContext(db));
            new DebugRoutes(hub).Map(routes);
            page.Map(routes);
        });
        await app.StartAsync();
        var backend = new MacBackend(db, hub, sessions, remoteMachine, outbox, app);
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
        if (_outbox is not null)
            await _outbox.DisposeAsync();
        await _sessions.DisposeAsync();
        if (_remote is not null)
            await _remote.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
    }
}
