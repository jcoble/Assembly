using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Files;

/// <summary><c>files/list</c> and <c>files/search</c> over the socket, for sessions whose folders are temp git repos.</summary>
public sealed class FilesSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _file = new();
    private readonly DirectoryInfo _checkouts = Directory.CreateTempSubdirectory("files-socket-");
    private AppDatabase _db = null!;
    private SessionStore _sessions = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        _sessions = SessionStore.Open(_db, TimeProvider.System, _checkouts.FullName);
        var calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new FilesFeature(_sessions).Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("files-token", calls, "test-machine", TimeProvider.System);
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
        _db.Dispose();
        _file.Dispose();
        _checkouts.Delete(recursive: true);
    }

    // Pins: files#1
    [Fact]
    public async Task A_session_s_item_names_its_folder_and_list_reads_it()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r");
        var root = _sessions.Item(session).Root!;
        Assert.Equal(Path.Combine(_checkouts.FullName, session.SessionId), root);
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "");
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "list", $$"""{"session":"{{session.SessionId}}","dir":""}""");
        var answer = await ReadAsync(socket);
        Assert.StartsWith("""{"t":"result","id":1""", answer);
        var entries = JsonDocument.Parse(answer).RootElement.GetProperty("result").GetProperty("entries");
        Assert.Equal(["src", "a.txt"], entries.EnumerateArray().Select(entry => entry.GetProperty("path").GetString()));
    }

    // Pins: files#7
    [Fact]
    public async Task Search_answers_a_page_of_names()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r");
        var root = _sessions.Item(session).Root!;
        GitFolder.Fill(root, "src/Program.cs", "readme.md");
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "search", $$"""{"session":"{{session.SessionId}}","query":"prog","cursor":0,"pageSize":10}""");
        var result = JsonDocument.Parse(await ReadAsync(socket)).RootElement.GetProperty("result");
        Assert.True(result.GetProperty("complete").GetBoolean());
        Assert.Equal("src/Program.cs", Assert.Single(result.GetProperty("matches").EnumerateArray()).GetProperty("relativePath").GetString());
    }

    // Pins: files#6, files#10
    [Fact]
    public async Task A_folder_not_made_yet_a_session_elsewhere_and_a_path_out_are_refused_with_why()
    {
        var waiting = _sessions.Create("claude", "https://github.com/o/r");
        var elsewhere = _sessions.Add(Guid.NewGuid().ToString(), "claude", "https://github.com/o/r", "box");
        Assert.Null(_sessions.Item(elsewhere).Root);
        var made = _sessions.Create("claude", "https://github.com/o/r");
        Directory.CreateDirectory(_sessions.Item(made).Root!);
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "list", $$"""{"session":"{{waiting.SessionId}}"}""");
        Assert.Contains("made on its first message", await ReadAsync(socket));
        await CallAsync(socket, 2, "list", $$"""{"session":"{{elsewhere.SessionId}}"}""");
        Assert.Contains("on box", await ReadAsync(socket));
        await CallAsync(socket, 3, "list", $$"""{"session":"{{made.SessionId}}","dir":"../.."}""");
        Assert.Contains("outside the session's folder", await ReadAsync(socket));
        await CallAsync(socket, 4, "list", """{"session":"nope"}""");
        Assert.Contains("unknown session", await ReadAsync(socket));
    }

    private async Task<ClientWebSocket> OpenAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"files-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    private static Task CallAsync(WebSocket socket, long id, string method, string args) =>
        SendAsync(socket, $$"""{"t":"call","id":{{id}},"feature":"files","method":"{{method}}","args":{{args}}}""");

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
