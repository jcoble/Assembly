using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;
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
    private readonly TopicSet _localTopics = new();
    private Func<WebSocket, Task>? _bulkRead;

    public async Task InitializeAsync()
    {
        var topics = new TopicSet();
        _remoteDb = AppDatabase.Open(_remoteFile.Path, TimeProvider.System, topics);
        _localDb = TestDatabase.Open(_localFile.Path);
        _remoteStore = RowStore.Open(_remoteDb);
        _remoteHub = new LiveHub(_remoteStore, topics: topics);
        _localHub = new LiveHub(RowStore.Open(_localDb), topics: _localTopics);
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
        _app.Map("/socket/bulk", async context =>
        {
            if (_bulkRead is null) await _bulk.HandleAsync(context);
            else
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await _bulkRead(socket);
            }
        });
        _app.MapPost("/sessions/s/send", () => new { runId = "run" });
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

    // Pins: remote#18
    [Fact]
    public async Task Remote_rows_keep_following_a_turn_the_desktop_started_until_its_rows_arrive()
    {
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rail = _localTopics.Subscribe(SessionsFeature.RailTopic, "s", _ => { }, (_, _) => copied.TrySetResult());
        _bulkRead = async socket =>
        {
            await ReadAsync(socket);
            await SendAsync(socket, """{"t":"welcome","protocol":1}""");
            await ReadAsync(socket);
            // The catch-up has read no rows. The send's user and started rows arrive afterwards,
            // through the live subscription, even if its empty read response is still in flight.
            var batch = new RowBatch();
            batch.Rows.Add(new NewRow("run", null, RowKind.User,
                JsonDocument.Parse("""{"text":"answer"}""").RootElement.Clone(), 1));
            batch.Rows.Add(new NewRow("run", null, RowKind.Turn,
                JsonDocument.Parse("""{"state":"started"}""").RootElement.Clone(), 1));
            _remoteHub.Publish("s", batch);
            await SendAsync(socket, """{"t":"result","id":1,"result":{"rows":[]}}""");
        };
        try
        {
            Assert.False(_localHub.HasSubscribers("s"));
            Assert.Equal("run", await _machine.SendAsync(_localSessions.Get("s")!, "answer", null, CancellationToken.None));
            await copied.Task.WaitAsync(Wait);
            Assert.False(_localHub.HasSubscribers("s"));
            Assert.Equal(new long[] { 1, 2 }, _localHub.RunRows("s", "run").Select(row => row.Seq));
            Assert.Equal("run", _localHub.OpenRun("s"));
        }
        finally { _localTopics.Unsubscribe(rail); }
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
        _bulk.DropAll();
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
        var frames = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<JsonElement> Subscribe(string session, long after,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            subscribed.TrySetResult();
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken)) yield return frame;
        }
        await using var follower = new RemoteFollower(_localHub, Subscribe, (_, _, _) =>
        {
            read.TrySetResult();
            return Task.FromResult<IReadOnlyList<StoredRow>>([]);
        }, _ => Task.CompletedTask, _ => Task.CompletedTask, (_, _) => { }, TextWriter.Null);
        follower.Watch("s");
        await subscribed.Task.WaitAsync(Wait);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, _time.Made);
        Assert.False(read.Task.IsCompleted);
        frames.Writer.TryWrite(JsonDocument.Parse("""{"t":"state","seq":0}""").RootElement.Clone());
        await read.Task.WaitAsync(Wait);
    }

    // Pins: remote#20. Intermediate updates to one item must survive a large catch-up.
    [Theory]
    [InlineData(502, 0)]
    [InlineData(4, 3 * 1024 * 1024)]
    public async Task Remote_rows_read_a_gap_too_big_to_push(int count, int payloadSize)
    {
        Publish(1, count, sameItem: true, payloadSize);
        using var viewer = _localHub.Subscribe("s", 0);
        _machine.Watch(_localSessions.Get("s")!);
        Assert.Equal(Enumerable.Range(1, count).Select(n => (long)n), await ReadSeqsAsync(viewer, count));
        Publish(count + 1, count + 1, sameItem: true);
        Assert.Equal(new long[] { count + 1 }, await ReadSeqsAsync(viewer, 1));
        Assert.False(viewer.Rows.TryRead(out _));
        Assert.Equal(0, _liveHits);
    }

    private void Publish(int from, int to, bool sameItem = false, int payloadSize = 0)
    {
        var batch = new RowBatch();
        for (var n = from; n <= to; n++)
            batch.Rows.Add(new NewRow("run", sameItem ? "tool" : $"tool-{n}", "tool",
                JsonDocument.Parse($$"""{"state":"running","summary":"update {{n}}{{new string('x', payloadSize)}}"}""").RootElement.Clone(), n));
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

    private static Task SendAsync(WebSocket socket, string frame) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReadAsync(WebSocket socket)
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
