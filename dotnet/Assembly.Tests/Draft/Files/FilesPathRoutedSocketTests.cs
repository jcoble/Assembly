using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Infrastructure.Clock;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Files;

/// <summary>A path target for this machine on a backend built as the desktop's is: with remote routes in front of the
/// handlers. The routes must let it through to them, on every call and for the order key.</summary>
public sealed class FilesPathRoutedSocketTests : IAsyncLifetime
{
    private readonly TempDatabase _file = new();
    private readonly DirectoryInfo _checkouts = Directory.CreateTempSubdirectory("files-routed-");
    private readonly BackgroundWork _work = new(TimeProvider.System, TextWriter.Null);
    private readonly GitFolder _folder = new();
    private AppDatabase _db = null!;
    private SessionStore _sessions = null!;
    private LiveHub _hub = null!;
    private MachinesFeature _machines = null!;
    private RemoteConnections _connections = null!;
    private FileRemoteRoutes _routes = null!;
    private WebApplication _app = null!;

    private string Target => Path.Combine(_folder.Root, "a.txt");

    public async Task InitializeAsync()
    {
        File.WriteAllText(Target, "old");
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        _hub = new LiveHub(RowStore.Open(_db), topics: topics);
        _sessions = SessionStore.Open(_db, TimeProvider.System, _checkouts.FullName);
        global::Assembly.Engine.Persistence.EngineSchema.Open(_db, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var status = new MachineStatus(topics, [], TimeProvider.System);
        var machines = new MachineSet(new FakeMachine(null, _sessions));
        var clock = new BackendClock(topics, TimeProvider.System, _work);
        var profiles = new MachineStore(_db);
        _machines = new MachinesFeature(machines, status, clock.Start, TimeProvider.System, store: profiles,
            connection: (name, connect, cancel) => _connections.ConnectionAsync(name, connect, cancel));
        _connections = new RemoteConnections(profiles, _db.Writer, machines, status, _hub, _sessions, topics, TextWriter.Null, _machines);
        var owners = new FileOwner(_sessions, _db);
        _routes = new FileRemoteRoutes(_db, owners, _connections, _work);
        var calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new FilesFeature(_sessions, owners, _work, _routes, new SessionRowSources(_db, _sessions, machines)).Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("files-token", calls, "test-machine", TimeProvider.System);
        _app.Map("/socket", endpoint.HandleAsync);
        var bulk = new SocketEndpoint("files-token", calls, "test-machine", TimeProvider.System, bulk: true);
        _app.Map("/socket/bulk", bulk.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(bulk.CloseAll);
        _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _work.DisposeAsync();
        await _connections.DisposeAsync();
        await _machines.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
        _file.Dispose();
        _checkouts.Delete(recursive: true);
        _folder.Dispose();
    }

    [Fact]
    public async Task The_routes_refuse_a_non_owner_peer_with_the_forbidden_code()
    {
        var phone = new CallContext(CancellationToken.None, "phone", Peer: new PhonePeer("phone"));
        var refused = await Assert.ThrowsAsync<CallFailed>(() => _routes.Invoke("owner", new ObserveRoot("s"),
            FilesJson.Default.ObserveRoot, FilesJson.Default.FileOwnership, phone, bulk: false, write: false));
        Assert.Equal("forbidden", refused.Reason);
    }
}
