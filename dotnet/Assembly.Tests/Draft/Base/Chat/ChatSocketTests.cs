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
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Projects;
using Assembly.Domain.Sessions;
using Assembly.Engine;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Projects;
using Assembly.Tests.Engine;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Preferences;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Infrastructure.Socket;
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
    private EngineRuntime _engine = null!;
    private ProjectStore _projects = null!;
    private EngineFixture _remoteOwner = null!;
    private SessionStore _remoteSessions = null!;
    private string _remoteHome = null!;
    private readonly AnswerDoor _answers = new();
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
        _projects = new ProjectStore(_db, _time);
        var roots = new ProjectRoots(_projects);
        _engine = EngineRuntime.Start(_db, topics, _time, new(() => 5, () => 10),
            cutover: new SessionCutover(_db, _sessions, _launcher, "checkouts", _time,
                projectRoots: roots, projectUse: roots, owningHome: _home.FullName));
        _remoteOwner = new();
        _remoteHome = _home.CreateSubdirectory("remote-owner").FullName;
        _remoteSessions = SessionStore.Open(_remoteOwner.Db, _time, _remoteHome);
        var remoteCutover = new SessionCutover(_remoteOwner.Db, _remoteSessions, _launcher, _remoteHome,
            _time, owningHome: _remoteHome);
        _remoteOwner.Db.Writer.Write("test.remote-schema", scope => { remoteCutover.EnsureSchema(scope); return 0; });
        remoteCutover.Attach(new EngineCommands(_remoteOwner.Machine, _remoteOwner.Limits,
            projectStart: remoteCutover.ProjectStart, projectProjection: _remoteSessions), _remoteOwner.Machine);
        _hub = new LiveHub(_rows, topics: topics, summaries: _summaries, summaryChanged: _sessions.PublishSummary);
        var rows = _rows;
        _here = new FakeMachine(null, _sessions);
        _box = new FakeMachine("box", _sessions);
        var machines = new MachineSet(_here, _box);
        _answers.Answer = request =>
        {
            var machine = _sessions.Get(request.Session)?.Machine is null ? _here : _box;
            if (machine.Down) throw new MachineUnreachable("box", "box is down");
            return Task.FromResult<Func<Assembly.Application.Common.Database.IWriteScope, bool>>(_ => !machine.Refuses);
        };
        // On the fake clock, so a test can run a call past its 30 s limit.
        var calls = new SocketDispatcher(_db.Writer, topics, _time, NullLogger.Instance);
        new SessionsFeature(_sessions, machines, new SessionRowSources(_db, _sessions, machines), roots,
            _engine.Machine, _home.FullName, remoteCreate: PrepareRemoteCreate).Register(calls);
        new SendFeature(_sessions, machines, _hub, _answers).Register(calls);
        new RowsFeature(_hub, rows, _sessions, machines).Register(calls);
        new ConfigFeature(_sessions, machines).Register(calls);
        new MachinesFeature(machines, new MachineStatus(topics, ["box"]), () => { }, TimeProvider.System).Register(calls);
        new PreferencesFeature(_db).Register(calls);
        _attachments = new AttachmentStore(Path.Combine(_home.FullName, "attachments"));
        new AttachmentsFeature(_sessions, machines, Staging).Register(calls);
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
        await _engine.DisposeAsync();
        _remoteOwner.Dispose();
        _db.Dispose();
        _file.Dispose();
        _home.Delete(recursive: true);
    }

    // These legacy unsourced routing/order cases hold the existing fake transport boundary.
    // The accepted session is still made by a real owning Engine writer. Source receipt proofs use TwoHosts.
    private async Task<Func<Assembly.Application.Common.Database.IWriteScope, SessionsFeature.Created>> PrepareRemoteCreate(
        SessionsFeature.CreateArgs args, CallContext call)
    {
        if (args.Machine != "box") throw new CallFailed("unknown machine");
        var known = _remoteSessions.FindByClient(args.ClientId, call.Cancellation, _remoteOwner.Machine);
        if (known.SessionId is null)
        {
            var agents = await _box.AgentsAsync(call.Cancellation);
            if (!agents.Any(agent => agent.Id == args.Agent)) throw new CallBadArguments("The agent is not configured on this owner.");
            call.Cancellation.ThrowIfCancellationRequested();
            _remoteOwner.Db.Writer.Write("test.remote-create", scope => _remoteSessions.Create(scope,
                args.Agent, args.ClientId, _remoteHome, null, null, initialConfig: args.InitialConfig));
            known = _remoteSessions.FindByClient(args.ClientId, call.Cancellation, _remoteOwner.Machine);
        }
        var accepted = _remoteSessions.Get(known.SessionId!)!;
        if (_sessions.Get(accepted.SessionId) is null)
            _sessions.Add(accepted.SessionId, accepted.Agent, accepted.RepoUrl, "box", workingFolder: accepted.WorkingFolder);
        return _ => new(accepted.SessionId, known.CheckoutOperationId, known.ProjectId);
    }

    private ProjectRecord CreateProject(string name)
    {
        var folder = _home.CreateSubdirectory(name).FullName;
        return _db.Writer.Write("test.project", scope => _projects.AddOrRestore(scope,
            "local", folder, name, null).Project);
    }

    private SessionInfo ReadyParent() => _db.Writer.Write("test.import-parent", scope =>
    {
        var id = Guid.NewGuid().ToString("D");
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        SessionImport.Import(scope, _engine.Machine, now,
            [new(id, null, "claude", null, _home.FullName, "Parent", "")]);
        _sessions.ProjectEngineSession(scope, new(id, "claude", _home.FullName, "Parent", null, now), null, _home.FullName);
        return _sessions.Get(scope, id)!;
    });

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
