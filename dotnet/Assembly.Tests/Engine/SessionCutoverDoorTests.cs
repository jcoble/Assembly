using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Needs;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Machines;
using Assembly.Tests.Features.Chat.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine;

public sealed class SessionCutoverDoorTests
{
    [Fact]
    public async Task Authenticated_socket_owners_create_the_same_untitled_checked_start()
    {
        await using var h = await Host.Start();
        var sentinel = Path.Combine(h.Home, "notes.md");
        await File.WriteAllTextAsync(sentinel, "owner draft");
        var before = Directory.EnumerateFileSystemEntries(h.Home).Select(Path.GetFileName).Order().ToArray();
        using var firstSocket = await h.Open();
        await Send(firstSocket, """{"t":"call","id":1,"feature":"sessions","method":"create","args":{"agent":"claude","clientId":"cutover_claude_1"}}""");
        var first = await Read(firstSocket);
        Assert.Equal("result", first.GetProperty("t").GetString());
        var firstId = first.GetProperty("result").GetProperty("sessionId").GetString()!;
        using var socket = await h.Open();
        await Send(socket, """{"t":"call","id":1,"feature":"sessions","method":"create","args":{"agent":"codex","clientId":"cutover_codex_1"}}""");
        var socketId = (await Read(socket)).GetProperty("result").GetProperty("sessionId").GetString()!;
        Assert.NotEqual(firstId, socketId);
        foreach (var id in new[] { firstId, socketId })
        {
            Assert.Null(h.Store.Get(id)!.Title);
            Assert.Equal("", h.F.Text("SELECT title FROM engine_session WHERE id=$id", new SqlArg("$id", id)));
        }
        Assert.Equal(2, h.F.Count("engine_session"));
        Assert.Equal(2, h.F.Events().Count(e => e.Kind == "session.started"));
        Assert.Equal(2, h.F.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        Assert.Equal(0, h.F.Count("engine_message")); Assert.Equal(0, h.Launcher.Launches);
        Assert.Equal("owner draft", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(h.Home).Select(Path.GetFileName).Order());
        Assert.False(Directory.Exists(Path.Combine(h.Home, "checkouts")));
    }

    [Fact]
    public async Task Registered_Window_Engine_start_projects_the_same_scope_item_and_nullable_model()
    {
        await using var h = await Host.Start(); using var socket = await h.Open();
        await Send(socket, """{"t":"sub","id":7,"topic":"sessions"}"""); await Read(socket);
        var command = JsonSerializer.Serialize(new WindowCommand(Guid.NewGuid(), "engine-root", 0, 0,
            new StartSession("claude", null, h.Home, "", null, null, null)), EngineSocketJson.Default.WindowCommand);
        await Send(socket, $$"""{"t":"call","id":2,"feature":"engine","method":"command","args":{{command}}}""");
        var change = await Read(socket);
        Assert.Equal("change", change.GetProperty("t").GetString());
        Assert.Equal(h.Home, change.GetProperty("item").GetProperty("root").GetString());
        Assert.Equal(JsonValueKind.Null, change.GetProperty("item").GetProperty("title").ValueKind);
        Assert.True((await Read(socket)).GetProperty("result").GetProperty("applied").GetBoolean());
        var session = h.Store.Get("engine-root")!;
        Assert.Equal(h.Home, session.RepoUrl); Assert.Equal(h.Home, h.Store.Root(session)); Assert.Equal(h.Home, h.Store.Item(session).Root);
        Assert.Equal(JsonValueKind.Null, h.Runtime.Reader.Entity("session", "engine-root", default)!.Value.GetProperty("model").ValueKind);
        Assert.Equal(1, h.F.Count("engine_session")); Assert.Equal(0, h.F.Count("engine_message"));
        Assert.Equal(0, h.Launcher.Launches);
    }

    [Theory]
    [InlineData("repo", false)]
    [InlineData("managed", false)]
    [InlineData("repo", true)]
    [InlineData("managed", true)]
    public async Task Registered_Window_cannot_claim_trusted_repository_or_managed_policy_even_before_remote_forward(string claim, bool remote)
    {
        await using var h = await Host.Start(); using var socket = await h.Open();
        var command = JsonSerializer.Serialize(new WindowCommand(Guid.NewGuid(), "blocked", 0, 0,
            new StartSession("claude", null, h.Home, "Title", null, null, null,
                RepoUrl: claim == "repo" ? "https://example.com/repo" : null, ManagedCheckout: claim == "managed" ? true : null),
            Machine: remote ? "unconfigured-peer" : null), EngineSocketJson.Default.WindowCommand);
        await Send(socket, $$"""{"t":"call","id":2,"feature":"engine","method":"command","args":{{command}}}""");
        var result = (await Read(socket)).GetProperty("result");
        Assert.False(result.GetProperty("applied").GetBoolean()); Assert.Equal("forbidden", result.GetProperty("error").GetString());
        Assert.Null(h.Store.Get("blocked")); Assert.Equal(0, h.F.Count("engine_session")); Assert.Equal(0, h.F.Count("engine_outbox"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Registered_Window_cannot_impersonate_model_or_title_observation(bool model)
    {
        await using var h = await Host.Start(); using var socket = await h.Open();
        EngineCommand observed = model ? new ReportSessionModel("wire") : new ReportSessionTitle("Title");
        var command = JsonSerializer.Serialize(new WindowCommand(Guid.NewGuid(), "blocked", 0, 0, observed), EngineSocketJson.Default.WindowCommand);
        await Send(socket, $$"""{"t":"call","id":2,"feature":"engine","method":"command","args":{{command}}}""");
        Assert.Equal("forbidden", (await Read(socket)).GetProperty("result").GetProperty("error").GetString());
        Assert.Equal(0, h.F.Count("engine_command_receipt")); Assert.Empty(h.F.Events());
    }

    private sealed class Host : IAsyncDisposable
    {
        public string Home { get; } = Directory.CreateTempSubdirectory("assembly-cutover-door-").FullName;
        private readonly Topics _topics = new();
        public EngineFixture F { get; }
        public FakeLauncher Launcher { get; } = new();
        public SessionStore Store { get; }
        public EngineRuntime Runtime { get; }
        public Uri Address { get; private set; } = null!;
        private readonly LiveHub _hub;
        private readonly BackgroundWork _work;
        private readonly SessionRegistry _registry;
        private readonly NeedAnswers _needs;
        private readonly WebApplication _app;
        private Host()
        {
            F = new(topics: _topics); Store = SessionStore.Open(F.Db, F.Time, Path.Combine(Home, "checkouts"));
            _hub = new LiveHub(RowStore.Open(F.Db)); _work = new(F.Time, TextWriter.Null);
            _registry = new(_hub, Store, Launcher, F.Time, TextWriter.Null, _work);
            Runtime = EngineRuntime.Start(F.Db, _topics, F.Time, F.Limits, cutover: new(F.Db, Store, Launcher, Path.Combine(Home, "checkouts"), F.Time, owningHome: Home));
            _needs = new(new(Runtime, F.Limits, F.Time), Runtime, _registry, F.Time);
            var attachments = new AttachmentStore(Path.Combine(Home, "attachments"));
            var machines = new MachineSet(new LocalMachine(_hub, Store, _registry, attachments));
            _app = ApiServer.Build(new(_hub, Store, machines, attachments, "token", Writer: F.Db.Writer, Topics: _topics,
                Features: [new SessionsFeature(Store, machines, owningMachine: Runtime.Machine, owningHome: Home), new EngineCalls(Runtime, F.Time, _needs)], Engine: Runtime), 0, out _);
        }
        public static async Task<Host> Start()
        {
            var host = new Host(); await host._app.StartAsync();
            host.Address = new(host._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return host;
        }
        public async Task<ClientWebSocket> Open()
        {
            var socket = new ClientWebSocket(); await socket.ConnectAsync(new($"ws://{Address.Authority}/socket"), CancellationToken.None);
            await Send(socket, """{"t":"hello","token":"token","protocol":1}"""); Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString());
            return socket;
        }
        public async ValueTask DisposeAsync()
        {
            await _needs.StopWaiting(); await _app.StopAsync(); await _app.DisposeAsync(); await _needs.DisposeAsync(); await _registry.DisposeAsync(); await _work.DisposeAsync();
            await Runtime.DisposeAsync(); _hub.Dispose(); F.Dispose(); Directory.Delete(Home, true);
        }
    }
    private static Task Send(ClientWebSocket socket, string text) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var bytes = new MemoryStream();
        var buffer = new byte[65536]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); bytes.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray()); return json.RootElement.Clone();
    }
}
