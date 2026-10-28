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
using Assembly.Infrastructure.Chat.Worktrees;
using Assembly.Infrastructure.Chat.SourceControl;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.SourceControl;

/// <summary>Worktree lists and git status over the socket, using temporary session repositories.</summary>
public sealed class GitSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _file = new();
    private readonly DirectoryInfo _checkouts = Directory.CreateTempSubdirectory("git-socket-");
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
        new WorktreesFeature(_sessions).Register(calls);
        new SourceControlFeature(_sessions).Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("git-token", calls, "test-machine", TimeProvider.System);
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

    // Pins: scm#1
    [Fact]
    public async Task Worktrees_lists_the_main_checkout_first_and_its_linked_worktree()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r");
        var root = _sessions.Item(session).Root!;
        await MakeRepoAsync(root);
        await GitAsync(root, "worktree", "add", "-b", "linked", Path.Combine(_checkouts.FullName, "linked"));
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "worktrees", "list", session.SessionId);
        using var answer = JsonDocument.Parse(await ReadAsync(socket));
        var worktrees = answer.RootElement.GetProperty("result").GetProperty("worktrees");
        Assert.Equal(2, worktrees.GetArrayLength());
        Assert.True(worktrees[0].GetProperty("isMain").GetBoolean());
        Assert.Equal("main", worktrees[0].GetProperty("branch").GetString());
        Assert.Equal("linked", worktrees[1].GetProperty("branch").GetString());
    }

    // Pins: scm#9, scm#10
    [Fact]
    public async Task Status_answers_branch_no_upstream_and_untracked_letters_as_strings()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r");
        var root = _sessions.Item(session).Root!;
        await MakeRepoAsync(root);
        await File.WriteAllTextAsync(Path.Combine(root, "new.txt"), "untracked");
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "git", "status", session.SessionId);
        using var answer = JsonDocument.Parse(await ReadAsync(socket));
        var status = answer.RootElement.GetProperty("result");
        Assert.Equal("main", status.GetProperty("branch").GetString());
        Assert.False(status.GetProperty("hasUpstream").GetBoolean());
        var file = Assert.Single(status.GetProperty("files").EnumerateArray());
        Assert.Equal("new.txt", file.GetProperty("path").GetString());
        Assert.Equal("?", file.GetProperty("index").GetString());
        Assert.Equal("?", file.GetProperty("worktree").GetString());
    }

    // Pins: scm#17
    [Fact]
    public async Task Diff_answers_working_changes_and_refuses_paths_outside_the_session()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r");
        var root = _sessions.Item(session).Root!;
        await MakeRepoAsync(root);
        await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "edited\n");
        using var socket = await OpenAsync();
        await SendAsync(socket, JsonSerializer.Serialize(new
        {
            t = "call",
            id = 1,
            feature = "git",
            method = "diff",
            args = new { session = session.SessionId, path = "tracked.txt" }
        }));
        using var answer = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Contains("+edited\n", answer.RootElement.GetProperty("result").GetProperty("unstaged").GetString());
        await SendAsync(socket, JsonSerializer.Serialize(new
        {
            t = "call",
            id = 2,
            feature = "git",
            method = "diff",
            args = new { session = session.SessionId, path = "../outside" }
        }));
        await AssertErrorAsync(socket, "that path is outside the session's folder");
    }

    // Pins: scm#6
    [Theory]
    [InlineData("worktrees", "list")]
    [InlineData("git", "status")]
    public async Task Unknown_remote_and_unmade_session_folders_are_refused(string feature, string method)
    {
        var waiting = _sessions.Create("claude", "https://github.com/o/r");
        var elsewhere = _sessions.Add(Guid.NewGuid().ToString(), "claude", "https://github.com/o/r", "box");
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, feature, method, "nope");
        await AssertErrorAsync(socket, "unknown session");
        await CallAsync(socket, 2, feature, method, elsewhere.SessionId);
        await AssertErrorAsync(socket, "this session's files are on box, which this window can't show yet");
        await CallAsync(socket, 3, feature, method, waiting.SessionId);
        await AssertErrorAsync(socket, "this session's folder is made on its first message");
    }

    private static async Task AssertErrorAsync(WebSocket socket, string message)
    {
        using var answer = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("error", answer.RootElement.GetProperty("t").GetString());
        Assert.Equal(message, answer.RootElement.GetProperty("message").GetString());
    }

    private static async Task MakeRepoAsync(string root)
    {
        Directory.CreateDirectory(root);
        await GitAsync(root, "init", "-b", "main");
        await GitAsync(root, "config", "user.name", "Socket Test");
        await GitAsync(root, "config", "user.email", "socket@example.test");
        await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "committed");
        await GitAsync(root, "add", "tracked.txt");
        await GitAsync(root, "-c", "commit.gpgsign=false", "commit", "-m", "Initial commit");
    }

    private static async Task<string> GitAsync(string root, params string[] args)
    {
        var records = new List<string>();
        await foreach (var record in new GitCli().ReadAsync(root, args, null, CancellationToken.None))
            records.Add(record);
        return string.Join('\0', records);
    }

    private async Task<ClientWebSocket> OpenAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"git-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    private static Task CallAsync(WebSocket socket, long id, string feature, string method, string session) =>
        SendAsync(socket, $$$"""{"t":"call","id":{{{id}}},"feature":"{{{feature}}}","method":"{{{method}}}","args":{"session":"{{{session}}}"}}""");

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
