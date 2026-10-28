using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Preferences;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base.Chat;

/// <summary>
/// Every chat call the window makes, over the socket (step 1b, before the cut), on fake machines: this one, and "box",
/// a remote one.
/// </summary>
public sealed partial class ChatSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _file = new();
    // The backend's home: its attachments and its uploads' staging files.
    private readonly DirectoryInfo _home = Directory.CreateTempSubdirectory("chat-socket-");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private AppDatabase _db = null!;
    private LiveHub _hub = null!;
    private RowStore _rows = null!;
    private SessionSummaries _summaries = null!;
    private SessionStore _sessions = null!;
    private FakeMachine _here = null!;
    private FakeMachine _box = null!;
    private AttachmentStore _attachments = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;
    private Uri _bulkAddress = null!;
    // Set once a socket's close has run every close hook, the features' first.
    private readonly TaskCompletionSource<string> _socketClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        _rows = RowStore.Open(_db);
        _summaries = SessionSummaries.Load(_rows);
        _sessions = SessionStore.Open(_db, _time, "checkouts", _summaries);
        _hub = new LiveHub(_rows, topics: topics, summaries: _summaries, summaryChanged: _sessions.PublishSummary);
        var rows = _rows;
        _here = new FakeMachine(null, _sessions);
        _box = new FakeMachine("box", _sessions);
        var machines = new MachineSet(_here, _box);
        // On the fake clock, so a test can run a call past its 30 s limit.
        var calls = new SocketDispatcher(_db.Writer, topics, _time, NullLogger.Instance);
        new SessionsFeature(_sessions, machines).Register(calls);
        new SendFeature(_sessions, machines, _hub).Register(calls);
        new RowsFeature(_hub, rows, _sessions, machines).Register(calls);
        new ConfigFeature(_sessions, machines).Register(calls);
        new MachinesFeature(machines, new MachineStatus(topics, ["box"]), () => { }, TimeProvider.System).Register(calls);
        new PreferencesFeature(_db).Register(calls);
        _attachments = new AttachmentStore(Path.Combine(_home.FullName, "attachments"));
        new AttachmentsFeature(_sessions, machines, _attachments, Staging).Register(calls);
        calls.Closed(connection => _socketClosed.TrySetResult(connection));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("chat-token", calls, "test-machine", TimeProvider.System);
        _app.Map("/socket", endpoint.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        var bulk = new SocketEndpoint("chat-token", calls, "test-machine", TimeProvider.System, bulk: true);
        _app.Map("/socket/bulk", bulk.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(bulk.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new Uri($"ws://{http.Authority}/socket");
        _bulkAddress = new Uri($"ws://{http.Authority}/socket/bulk");
    }

    public async Task DisposeAsync()
    {
        await StopRealAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
        _file.Dispose();
        _home.Delete(recursive: true);
    }

    private string Staging => Path.Combine(_home.FullName, "staging", "attachments");

    private Task<ClientWebSocket> OpenAsync() => ConnectAsync(_address);

    // The socket for the large calls: attachments, rows/page and rows/tool.
    private Task<ClientWebSocket> OpenBulkAsync() => ConnectAsync(_bulkAddress);

    private static async Task<ClientWebSocket> ConnectAsync(Uri address)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(address, CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"chat-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    // A call's frame, with the args as JSON.
    private static Task CallAsync(WebSocket socket, long id, string feature, string method, string args) =>
        SendAsync(socket, $$"""{"t":"call","id":{{id}},"feature":"{{feature}}","method":"{{method}}","args":{{args}}}""");

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReadAsync(WebSocket socket)
    {
        using var message = new MemoryStream();
        var buffer = new byte[256 * 1024];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(Wait);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            message.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }
}
