using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base;

/// <summary>
/// <c>machines/list</c> over the socket, with a real remote machine whose far end is a small stub server: whether each
/// machine answered last time, and why not when it didn't.
/// </summary>
public sealed class MachinesSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string Local = """{"name":null,"kind":"local","reachable":true,"reason":null}""";

    private readonly TempDatabase _file = new();
    private readonly StubRemote _stub = new();
    private AppDatabase _db = null!;
    private LiveHub _hub = null!;
    private RemoteMachine _box = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        await _stub.StartAsync();
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        _hub = new LiveHub(RowStore.Open(_db), topics: topics);
        var sessions = SessionStore.Open(_db, TimeProvider.System);
        var status = new MachineStatus(["box"]);
        _box = new RemoteMachine("box", _stub.Address, "box-token", _hub, sessions, TextWriter.Null, status);
        var machines = new MachineSet(new FakeMachine(null, sessions), _box);
        var calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new MachinesFeature(machines, status).Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("machines-token", calls, "test-machine", TimeProvider.System);
        _app.Map("/socket", endpoint.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new Uri($"ws://{http.Authority}/socket");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _box.DisposeAsync();
        await _stub.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
        _file.Dispose();
    }

    [Fact]
    public async Task Machines_list_answers_each_machine_and_a_remote_starts_not_checked_yet()
    {
        using var socket = await OpenAsync();

        Assert.Equal(List(1, Box(false, "not checked yet")), await ListAsync(socket, 1));
    }

    [Fact]
    public async Task A_500_answers_failed_and_leaves_the_machine_reachable()
    {
        _stub.AgentsStatus = 500;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "machines", "agents", """{"machine":"box"}""");
        var error = await ReadAsync(socket);
        Assert.Contains("\"code\":\"failed\"", error);
        Assert.Contains("box answered 500: boom", error);

        Assert.Equal(List(2, Box(true, null)), await ListAsync(socket, 2));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_401_or_403_answers_unreachable_with_the_token_reason(int refused)
    {
        _stub.AgentsStatus = refused;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "machines", "agents", """{"machine":"box"}""");
        var error = await ReadAsync(socket);
        Assert.Contains("\"code\":\"unreachable\"", error);
        Assert.Contains("the remote refused this computer's token", error);

        Assert.Equal(List(2, Box(false, "the remote refused this computer's token")), await ListAsync(socket, 2));
    }

    [Fact]
    public async Task A_transport_failure_is_didnt_answer_and_the_next_answer_is_reachable()
    {
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "machines", "agents", """{"machine":"box"}""");
        Assert.Contains("\"result\":", await ReadAsync(socket));
        Assert.Equal(List(2, Box(true, null)), await ListAsync(socket, 2));

        _stub.Drop = true;
        await CallAsync(socket, 3, "machines", "agents", """{"machine":"box"}""");
        Assert.Contains("\"code\":\"unreachable\"", await ReadAsync(socket));
        Assert.Equal(List(4, Box(false, "didn't answer")), await ListAsync(socket, 4));

        _stub.Drop = false;
        await CallAsync(socket, 5, "machines", "agents", """{"machine":"box"}""");
        Assert.Contains("\"result\":", await ReadAsync(socket));
        Assert.Equal(List(6, Box(true, null)), await ListAsync(socket, 6));
    }

    [Fact]
    public async Task A_remote_with_another_row_format_stays_unreachable_with_its_reason_on_each_call()
    {
        _stub.Format = 99;
        var reason = $"stores rows in format 99; this computer reads format {RowStore.FormatVersion}";
        using var socket = await OpenAsync();

        for (var id = 1; id <= 4; id += 2)
        {
            await CallAsync(socket, id, "machines", "agents", """{"machine":"box"}""");
            var error = await ReadAsync(socket);
            Assert.Contains("\"code\":\"unreachable\"", error);
            Assert.Contains(reason, error);
            Assert.Equal(List(id + 1, Box(false, reason)), await ListAsync(socket, id + 1));
        }
        Assert.Equal((2, 0), (_stub.VersionHits, _stub.AgentsHits));
    }

    private static string Box(bool reachable, string? reason) =>
        $$"""{"name":"box","kind":"remote","reachable":{{(reachable ? "true" : "false")}},"reason":{{(reason is null ? "null" : $"\"{reason}\"")}}}""";

    private static string List(long id, string box) =>
        $$$"""{"t":"result","id":{{{id}}},"result":{"machines":[{{{Local}}},{{{box}}}]}}""";

    private static async Task<string> ListAsync(WebSocket socket, long id)
    {
        await CallAsync(socket, id, "machines", "list", "{}");
        return await ReadAsync(socket);
    }

    private async Task<ClientWebSocket> OpenAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"machines-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    private static Task CallAsync(WebSocket socket, long id, string feature, string method, string args) =>
        SendAsync(socket, $$"""{"t":"call","id":{{id}},"feature":"{{feature}}","method":"{{method}}","args":{{args}}}""");

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
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            message.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);
        // The error frames escape an apostrophe; compare on one spelling.
        return Encoding.UTF8.GetString(message.ToArray()).Replace("\\u0027", "'");
    }

    // The remote's far end: /version and /agents, each able to answer an error status or drop the connection.
    private sealed class StubRemote : IAsyncDisposable
    {
        private WebApplication _app = null!;

        public Uri Address { get; private set; } = null!;
        public int Format { get; set; } = RowStore.FormatVersion;
        public int AgentsStatus { get; set; } = 200;
        public bool Drop { get; set; }
        public int VersionHits;
        public int AgentsHits;

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.MapGet("/version", () =>
            {
                Interlocked.Increment(ref VersionHits);
                return Results.Text($$"""{"formatVersion":{{Format}},"commit":"abc"}""", "application/json");
            });
            _app.MapGet("/agents", (HttpContext context) =>
            {
                Interlocked.Increment(ref AgentsHits);
                if (Drop)
                {
                    context.Abort();
                    return Results.Empty;
                }
                return AgentsStatus == 200
                    ? Results.Text("""{"agents":[{"id":"claude","name":"Claude"}]}""", "application/json")
                    : Results.Text("boom", statusCode: AgentsStatus);
            });
            await _app.StartAsync();
            Address = new Uri(_app.Urls.Single());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
