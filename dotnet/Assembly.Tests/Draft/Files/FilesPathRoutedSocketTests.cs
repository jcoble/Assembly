using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common;
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
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

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
    private Uri _address = null!;

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
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new Uri($"ws://{http.Authority}/socket");
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
    public async Task Owner_read_and_release_for_this_machine_reach_the_handlers()
    {
        await File.WriteAllTextAsync(Target, new string('x', FileRead.ChunkBytes + 1));
        using var normal = await OpenAsync();
        using var bulk = await OpenAsync(bulk: true);
        await CallAsync(normal, 1, "owner", $$$"""{"target":{"path":"{{{Target}}}"}}""");
        var owner = Result(await ReadAsync(normal));
        Assert.Equal("a.txt", owner.GetProperty("file").GetString());
        await CallAsync(bulk, 2, "read", $$$"""{"target":{"path":"{{{Target}}}"},"expectedOwner":{{{owner.GetRawText()}}}}""");
        var read = Result(await ReadAsync(bulk));
        Assert.NotEqual(JsonValueKind.Null, read.GetProperty("nextOffset").ValueKind);
        await CallAsync(bulk, 3, "release", $$$"""{"target":{"path":"{{{Target}}}"},"revision":"{{{read.GetProperty("revision").GetString()}}}","expectedOwner":{{{owner.GetRawText()}}}}""");
        Assert.True(Result(await ReadAsync(bulk)).GetProperty("released").GetBoolean());
    }

    [Fact]
    public async Task A_save_for_this_machine_reaches_the_handlers()
    {
        using var bulk = await OpenAsync(bulk: true);
        await CallAsync(bulk, 1, "beginSave", $$$"""{"target":{"path":"{{{Target}}}"},"expectedRevision":"{{{await FileSave.RevisionAsync(Target, default)}}}","size":3}""");
        var upload = Result(await ReadAsync(bulk)).GetProperty("upload").GetString();
        await CallAsync(bulk, 2, "saveChunk", $$$"""{"upload":"{{{upload}}}","offset":0,"data":"{{{Convert.ToBase64String(Encoding.UTF8.GetBytes("new"))}}}"}""");
        await ReadAsync(bulk);
        await CallAsync(bulk, 3, "commitSave", $$$"""{"upload":"{{{upload}}}"}""");
        Assert.Equal("saved", Result(await ReadAsync(bulk)).GetProperty("state").GetString());
        Assert.Equal("new", await File.ReadAllTextAsync(Target));
    }

    [Fact]
    public async Task The_handlers_answer_a_refusal_with_routes_in_front_too()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r");
        Directory.CreateDirectory(_sessions.Item(session).Root!);
        using var normal = await OpenAsync();
        await CallAsync(normal, 1, "owner", """{"target":{"path":"/proc/self/status"}}""");
        Assert.Equal("not_regular", Code(await ReadAsync(normal)));
        await CallAsync(normal, 2, "owner", "{}");
        Assert.Equal("bad_args", Code(await ReadAsync(normal)));
        await CallAsync(normal, 3, "owner", $$$"""{"session":"{{{session.SessionId}}}","target":{"path":"{{{Target}}}"}}""");
        Assert.Equal("bad_args", Code(await ReadAsync(normal)));
        await CallAsync(normal, 4, "search", $$$"""{"target":{"path":"{{{Target}}}"},"query":"a"}""");
        Assert.Equal("bad_args", Code(await ReadAsync(normal)));
    }

    [Fact]
    public async Task A_session_call_for_this_machine_still_runs_with_routes_in_front()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r");
        var root = _sessions.Item(session).Root!;
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "one\n");
        using var bulk = await OpenAsync(bulk: true);
        await CallAsync(bulk, 1, "read", $$$"""{"session":"{{{session.SessionId}}}","path":"a.txt"}""");
        Assert.Equal("one\n", Result(await ReadAsync(bulk)).GetProperty("content").GetString());
    }

    [Fact]
    public void The_order_key_names_a_path_only_for_another_machine()
    {
        var revision = new string('0', 64);
        BeginSave Save(string? machine) => new(null, null, revision, 0, Target: new PathTarget("/x/a.txt", machine));
        Assert.Null(_routes.Order(Save(null), FilesJson.Default.BeginSave));
        Assert.Equal("files-path:box:/x/a.txt", _routes.Order(Save("box"), FilesJson.Default.BeginSave));
        Assert.Null(_routes.Order(new BeginSave("s", "a.txt", revision, 0), FilesJson.Default.BeginSave));
    }

    private static JsonElement Result(string answer) => JsonDocument.Parse(answer).RootElement.GetProperty("result");
    private static string? Code(string answer) => JsonDocument.Parse(answer).RootElement.GetProperty("code").GetString();

    private async Task<ClientWebSocket> OpenAsync(bool bulk = false)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(bulk ? new Uri(_address + "/bulk") : _address, CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"files-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    private static Task CallAsync(WebSocket socket, long id, string method, string args) =>
        SendAsync(socket, $$$"""{"t":"call","id":{{{id}}},"feature":"files","method":"{{{method}}}","args":{{{args}}}}""");

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReadAsync(WebSocket socket)
    {
        using var message = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(Wait);
            message.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }
}
