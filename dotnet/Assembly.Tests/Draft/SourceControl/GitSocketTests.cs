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
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
using Assembly.Infrastructure.Persistence;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Socket;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.SourceControl;

/// <summary>Worktree lists and git status over the socket, using temporary session repositories.</summary>
public sealed partial class GitSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _file = new();
    private readonly DirectoryInfo _checkouts = Directory.CreateTempSubdirectory("git-socket-");
    private readonly Assembly.Application.Common.BackgroundWork _gitWork = new(TimeProvider.System, TextWriter.Null);
    private GitOperations _operations = null!;
    private AppDatabase _db = null!;
    private SessionStore _sessions = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        _sessions = SessionStore.Open(_db, TimeProvider.System, _checkouts.FullName);
        var machine = EngineSchema.Open(_db, TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
        var calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new WorktreesFeature(_sessions).Register(calls);
        _operations = new(_gitWork, topics, TimeProvider.System, machine);
        var routes = new SourceControlFeature(_sessions, _db, _operations, null, null, new GitCli());
        routes.Register(calls);
        new SourceControlWrites(_sessions, new GitCli(), new CheckoutWrites(), TimeProvider.System, routes).Register(calls);
        var prGit = new GitCli();
        var prReader = new PullRequestReader(prGit, new SocketGh(), _checkouts.FullName, new GitTextPatch(prGit));
        var projectRoots = new Assembly.Infrastructure.Projects.ProjectRoots(new Assembly.Application.Features.Projects.ProjectStore(_db, TimeProvider.System));
        var sources = new Assembly.Infrastructure.Engine.Sessions.SessionRowSources(_db, _sessions,
            new Assembly.Application.Common.Machines.MachineSet(new Assembly.Tests.Draft.Base.Chat.FakeMachine(null, _sessions)));
        new PullRequestsFeature(_sessions, prReader, routes, new(_db, projectRoots, sources, machine, prReader, null, null)).Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("git-token", calls, machine, TimeProvider.System);
        _app.Map("/socket", endpoint.HandleAsync);
        var bulkEndpoint = new SocketEndpoint("git-token", calls, machine, TimeProvider.System, bulk: true);
        _app.Map("/socket/bulk", bulkEndpoint.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(bulkEndpoint.CloseAll);
        _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new Uri($"ws://{http.Authority}/socket");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _operations.DisposeAsync();
        await _gitWork.DisposeAsync();
        _db.Dispose();
        _file.Dispose();
        _checkouts.Delete(recursive: true);
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

    [Fact]
    public async Task Status_answers_base_branch_first_commit_age_and_per_file_line_counts()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r");
        var root = _sessions.Item(session).Root!;
        await MakeRepoAsync(root);
        await File.WriteAllTextAsync(Path.Combine(root, "old name.txt"), "line one\n");
        await GitAsync(root, "add", "old name.txt");
        await GitAsync(root, "-c", "commit.gpgsign=false", "commit", "-m", "Add rename target");
        using var socket = await OpenAsync();

        // On the base branch itself: no baseBranch and no branchCreatedAt.
        await CallAsync(socket, 1, "git", "status", session.SessionId);
        using (var onBase = JsonDocument.Parse(await ReadAsync(socket)))
        {
            var result = onBase.RootElement.GetProperty("result");
            Assert.Equal("main", result.GetProperty("branch").GetString());
            Assert.False(result.TryGetProperty("baseBranch", out _));
            Assert.False(result.TryGetProperty("branchCreatedAt", out _));
        }

        // Fresh feature branch with no commits of its own yet: baseBranch without branchCreatedAt.
        await GitAsync(root, "switch", "-c", "feat/counts");
        await CallAsync(socket, 2, "git", "status", session.SessionId);
        using (var emptyBranch = JsonDocument.Parse(await ReadAsync(socket)))
        {
            var result = emptyBranch.RootElement.GetProperty("result");
            Assert.Equal("feat/counts", result.GetProperty("branch").GetString());
            Assert.Equal("main", result.GetProperty("baseBranch").GetString());
            Assert.False(result.TryGetProperty("branchCreatedAt", out _));
        }

        // Two commits on the branch: branchCreatedAt is the first own commit's time, not HEAD's.
        await GitAsync(root, "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "First branch commit");
        var firstCommitMs = long.Parse((await GitAsync(root, "log", "-1", "--format=%ct")).Trim(), System.Globalization.CultureInfo.InvariantCulture) * 1000;
        await Task.Delay(1100);
        await GitAsync(root, "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Second branch commit");
        var headCommitMs = long.Parse((await GitAsync(root, "log", "-1", "--format=%ct")).Trim(), System.Globalization.CultureInfo.InvariantCulture) * 1000;
        Assert.True(headCommitMs > firstCommitMs);

        // Staged add, staged+unstaged edit, staged rename with spaces, staged binary, and untracked file.
        await File.WriteAllTextAsync(Path.Combine(root, "plans.ts"), "export const a = 1;\nexport const b = 2;\n");
        await GitAsync(root, "add", "plans.ts");
        await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "staged one\nstaged two\n");
        await GitAsync(root, "add", "tracked.txt");
        await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "staged one\nstaged two\nunstaged three\n");
        await GitAsync(root, "mv", "old name.txt", "new name.txt");
        await File.WriteAllTextAsync(Path.Combine(root, "new name.txt"), "line one\nline two\n");
        await GitAsync(root, "add", "new name.txt");
        await File.WriteAllBytesAsync(Path.Combine(root, "bin.dat"), [0, 1, 2, 3, 0, 255]);
        await GitAsync(root, "add", "bin.dat");
        await File.WriteAllTextAsync(Path.Combine(root, "scratch.txt"), "not tracked\n");

        await CallAsync(socket, 3, "git", "status", session.SessionId);
        using var populated = JsonDocument.Parse(await ReadAsync(socket));
        var status = populated.RootElement.GetProperty("result");
        Assert.Equal("main", status.GetProperty("baseBranch").GetString());
        Assert.Equal(firstCommitMs, status.GetProperty("branchCreatedAt").GetInt64());

        var byPath = status.GetProperty("files").EnumerateArray().ToDictionary(entry => entry.GetProperty("path").GetString()!);
        Assert.Equal(2, byPath["plans.ts"].GetProperty("stagedAdded").GetInt32());
        Assert.Equal(0, byPath["plans.ts"].GetProperty("stagedRemoved").GetInt32());
        Assert.False(byPath["plans.ts"].TryGetProperty("unstagedAdded", out _));

        Assert.Equal(2, byPath["tracked.txt"].GetProperty("stagedAdded").GetInt32());
        Assert.Equal(1, byPath["tracked.txt"].GetProperty("stagedRemoved").GetInt32());
        Assert.Equal(1, byPath["tracked.txt"].GetProperty("unstagedAdded").GetInt32());
        Assert.Equal(0, byPath["tracked.txt"].GetProperty("unstagedRemoved").GetInt32());

        Assert.Equal(1, byPath["new name.txt"].GetProperty("stagedAdded").GetInt32());
        Assert.Equal(0, byPath["new name.txt"].GetProperty("stagedRemoved").GetInt32());

        Assert.False(byPath["bin.dat"].TryGetProperty("stagedAdded", out _));
        Assert.False(byPath["scratch.txt"].TryGetProperty("stagedAdded", out _));
        Assert.False(byPath["scratch.txt"].TryGetProperty("unstagedAdded", out _));
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
        await SendAsync(socket, JsonSerializer.Serialize(new
        {
            t = "call",
            id = 3,
            feature = "git",
            method = "diff",
            args = new { session = session.SessionId, path = "." }
        }));
        await AssertErrorAsync(socket, "expected a file in the session's folder");
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
