using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Remote;

public sealed class RemoteRowsSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly TempDatabase _remoteFile = new();
    private readonly TempDatabase _localFile = new();
    private readonly CountingTime _time = new();
    private readonly TaskCompletionSource _legacy = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AppDatabase _remoteDb = null!, _localDb = null!;
    private LiveHub _remoteHub = null!, _localHub = null!;
    private RowStore _remoteStore = null!;
    private SessionStore _localSessions = null!;
    private RemoteMachine _machine = null!;
    private SocketEndpoint _socket = null!, _bulk = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;
    private int _liveHits;

    public async Task InitializeAsync()
    {
        var topics = new TopicSet();
        _remoteDb = AppDatabase.Open(_remoteFile.Path, TimeProvider.System, topics);
        _localDb = TestDatabase.Open(_localFile.Path);
        _remoteStore = RowStore.Open(_remoteDb);
        _remoteHub = new LiveHub(_remoteStore, topics: topics);
        _localHub = new LiveHub(RowStore.Open(_localDb));
        var sessions = SessionStore.Open(_remoteDb, TimeProvider.System, "checkouts");
        sessions.Add("s", "claude", "https://github.com/o/r", "box");
        _localSessions = SessionStore.Open(_localDb, TimeProvider.System, "checkouts");
        _localSessions.Add("s", "claude", "https://github.com/o/r", "box");
        var calls = new SocketDispatcher(_remoteDb.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new RowsFeature(_remoteHub, _remoteStore, sessions, new MachineSet(new FakeMachine(null, sessions))).Register(calls);
        _socket = new SocketEndpoint("token", calls, "box", TimeProvider.System);
        _bulk = new SocketEndpoint("token", calls, "box", TimeProvider.System, bulk: true);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/socket", _socket.HandleAsync);
        _app.Map("/socket/bulk", _bulk.HandleAsync);
        _app.MapGet("/version", () => new { formatVersion = RowStore.FormatVersion });
        _app.Map("/live", context =>
        {
            Interlocked.Increment(ref _liveHits);
            _legacy.TrySetResult();
            context.Response.StatusCode = 410;
            return Task.CompletedTask;
        });
        await _app.StartAsync();
        _address = new Uri(_app.Urls.Single());
        _machine = new RemoteMachine("box", _address, "token", _localHub, _localSessions, TextWriter.Null,
            new MachineStatus(new TopicSet(), ["box"]));
    }

    // Pins: remote#18, remote#8
    [Fact]
    public async Task Remote_rows_arrive_on_the_machine_socket_while_a_turn_runs()
    {
        using var viewer = _localHub.Subscribe("s", 0);
        _machine.Watch(_localSessions.Get("s")!);
        Publish(1, 5);
        Assert.Equal(Enumerable.Range(1, 5).Select(n => (long)n), await ReadSeqsAsync(viewer, 5));
        Assert.Equal(0, _liveHits);
    }

    // Pins: remote#20
    [Fact]
    public async Task Remote_rows_catch_up_after_the_socket_drops_without_a_gap_or_a_repeat()
    {
        using var viewer = _localHub.Subscribe("s", 0);
        Publish(1, 5);
        _machine.Watch(_localSessions.Get("s")!);
        await ReadSeqsAsync(viewer, 5);
        _socket.DropAll();
        Publish(6, 9);
        Assert.Equal(new long[] { 6, 7, 8, 9 }, await ReadSeqsAsync(viewer, 4));
        Assert.False(viewer.Rows.TryRead(out _));
        Assert.Equal(0, _liveHits);
    }

    // Pins: remote#8
    [Fact]
    public async Task Remote_follower_has_no_timer_of_its_own()
    {
        using var viewer = _localHub.Subscribe("s", 0);
        await using var follower = new RemoteFollower(_address, "token", _localHub, _ => Task.CompletedTask,
            () => { }, _ => Task.CompletedTask, (_, _) => { }, TextWriter.Null, _time);
        follower.Watch("s");
        await _time.Created.Task.WaitAsync(Wait);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, _time.Made);
    }

    // Pins: remote#20. Intermediate updates to one item must survive a large catch-up.
    [Fact]
    public async Task Remote_rows_read_a_gap_too_big_to_push()
    {
        Publish(1, 502, sameItem: true);
        using var viewer = _localHub.Subscribe("s", 0);
        _machine.Watch(_localSessions.Get("s")!);
        Assert.Equal(Enumerable.Range(1, 502).Select(n => (long)n), await ReadSeqsAsync(viewer, 502));
        Publish(503, 503, sameItem: true);
        Assert.Equal(new long[] { 503 }, await ReadSeqsAsync(viewer, 1));
        Assert.False(viewer.Rows.TryRead(out _));
        Assert.Equal(0, _liveHits);
    }

    private void Publish(int from, int to, bool sameItem = false)
    {
        var batch = new RowBatch();
        for (var n = from; n <= to; n++)
            batch.Rows.Add(new NewRow("run", sameItem ? "tool" : $"tool-{n}", "tool",
                JsonDocument.Parse($$"""{"state":"running","summary":"update {{n}}"}""").RootElement.Clone(), n));
        _remoteHub.Publish("s", batch);
    }

    private async Task<long[]> ReadSeqsAsync(LiveSubscription viewer, int count)
    {
        var result = new List<long>();
        while (result.Count < count)
        {
            var read = viewer.Rows.ReadAsync().AsTask();
            await Task.WhenAny(read, _legacy.Task).WaitAsync(Wait);
            Assert.Equal(0, Volatile.Read(ref _liveHits));
            result.Add((await read.WaitAsync(Wait)).Seq);
        }
        return [.. result];
    }

    private async Task<ClientWebSocket> OpenAsync(bool bulk)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{_address.Authority}/socket{(bulk ? "/bulk" : "")}"), CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","protocol":1,"token":"token"}""");
        using var welcome = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("welcome", welcome.RootElement.GetProperty("t").GetString());
        return socket;
    }

    private static Task SendAsync(ClientWebSocket socket, string frame) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReadAsync(ClientWebSocket socket)
    {
        using var deadline = new CancellationTokenSource(Wait);
        using var frame = new MemoryStream();
        var chunk = new byte[16384];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(chunk, deadline.Token);
            frame.Write(chunk, 0, received.Count);
        } while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(frame.ToArray());
    }

    public async Task DisposeAsync()
    {
        await _machine.DisposeAsync();
        _socket.CloseAll();
        _bulk.CloseAll();
        await _app.DisposeAsync();
        _localHub.Dispose();
        _remoteHub.Dispose();
        _localDb.Dispose();
        _remoteDb.Dispose();
        _localFile.Dispose();
        _remoteFile.Dispose();
    }

    private sealed class CountingTime : FakeTimeProvider
    {
        public int Made;
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref Made);
            Created.TrySetResult();
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
