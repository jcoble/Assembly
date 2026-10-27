using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Infrastructure.Socket;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base;

/// <summary>
/// A session's rows over the socket (plan 5.1, 5.7, C1): the <c>rows</c> topic with <c>after</c>, its <c>rows</c>
/// frames, and <c>rows/page</c>.
/// </summary>
public sealed class RowsSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _file = new();
    private AppDatabase _db = null!;
    private LiveHub _hub = null!;
    private RowStore _store = null!;
    private SessionStore _sessions = null!;
    private FakeMachine _box = null!;
    private TopicSet _topics = null!;
    private SocketDispatcher _calls = null!;
    private SocketEndpoint _endpoint = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        var topics = _topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        var store = _store = RowStore.Open(_db);
        _hub = new LiveHub(store, topics: topics);
        _calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        var sessions = _sessions = SessionStore.Open(_db, TimeProvider.System);
        _box = new FakeMachine("box", sessions);
        new RowsFeature(_hub, store, sessions, new MachineSet(new FakeMachine(null, sessions), _box)).Register(_calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        _endpoint = new SocketEndpoint("rows-token", _calls, "test-machine", TimeProvider.System);
        _app.Map("/socket", _endpoint.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(_endpoint.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new Uri($"ws://{http.Authority}/socket");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
        _file.Dispose();
    }

    [Fact]
    public async Task A_first_open_starts_at_the_sessions_last_seq_then_gets_each_new_row()
    {
        Commit("s", 3);
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"rows","key":"s"}""");
        Assert.Equal("""{"t":"state","sub":1,"seq":3,"items":[]}""", await ReadAsync(socket));

        Commit("s", 1);
        AssertRow(await ReadAsync(socket), sub: 1, seq: 4, prevSeq: 3);
    }

    [Fact]
    public async Task A_resume_gets_the_rows_after_its_seq_in_order_then_the_new_ones()
    {
        Commit("s", 5);
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"rows","key":"s","after":2}""");
        Assert.Equal("""{"t":"state","sub":1,"seq":2,"items":[]}""", await ReadAsync(socket));
        for (var seq = 3; seq <= 5; seq++)
            AssertRow(await ReadAsync(socket), sub: 1, seq, prevSeq: seq - 1);

        Commit("s", 1);
        AssertRow(await ReadAsync(socket), sub: 1, seq: 6, prevSeq: 5);
    }

    [Fact]
    public async Task A_resume_catches_up_a_page_and_one_row_more_starts_again_from_the_last_seq()
    {
        Commit("s", RowsFeature.PageLimit);
        using (var socket = await OpenAsync())
        {
            await SendAsync(socket, """{"t":"sub","id":1,"topic":"rows","key":"s","after":0}""");
            Assert.Equal("""{"t":"state","sub":1,"seq":0,"items":[]}""", await ReadAsync(socket));
            for (var seq = 1; seq <= RowsFeature.PageLimit; seq++)
                AssertRow(await ReadAsync(socket), sub: 1, seq, prevSeq: seq - 1);
        }

        Commit("s", 1);
        using var behind = await OpenAsync();
        await SendAsync(behind, """{"t":"sub","id":1,"topic":"rows","key":"s","after":0}""");
        Assert.Equal($$"""{"t":"state","sub":1,"seq":{{RowsFeature.PageLimit + 1}},"items":[]}""", await ReadAsync(behind));
        Commit("s", 1);
        AssertRow(await ReadAsync(behind), sub: 1, RowsFeature.PageLimit + 2, prevSeq: RowsFeature.PageLimit + 1);
    }

    // A copied or restored database can be behind what the page applied: it starts again from the last seq.
    [Fact]
    public async Task An_after_past_the_sessions_last_seq_starts_again_from_the_last_seq()
    {
        Commit("s", 3);
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"rows","key":"s","after":9}""");
        Assert.Equal("""{"t":"state","sub":1,"seq":3,"items":[]}""", await ReadAsync(socket));

        Commit("s", 1);
        AssertRow(await ReadAsync(socket), sub: 1, seq: 4, prevSeq: 3);
    }

    // A row committed before the snapshot but published after it (5.7, B5's test 6): the snapshot already has it, so
    // the published copy isn't sent again.
    [Fact]
    public async Task A_row_the_snapshot_already_has_isnt_sent_again_when_it_is_published()
    {
        Commit("s", 2);
        var batch = new RowBatch();
        batch.Rows.Add(new NewRow("run-1", null, RowKind.User, JsonSerializer.SerializeToElement(new { text = "late" }), 1));
        var late = Assert.Single(_store.Commit("s", batch));
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"rows","key":"s","after":1}""");
        Assert.Equal("""{"t":"state","sub":1,"seq":1,"items":[]}""", await ReadAsync(socket));
        AssertRow(await ReadAsync(socket), sub: 1, seq: 2, prevSeq: 1);
        AssertRow(await ReadAsync(socket), sub: 1, seq: 3, prevSeq: 2);

        _topics.Publish(RowsFeature.Topic, "s", RowWire.ToJson(late));
        Commit("s", 1);
        AssertRow(await ReadAsync(socket), sub: 1, seq: 4, prevSeq: 3);
    }

    // The socket closes past 16 MB queued, so a catch-up that big would loop: past RowsFeature.CatchUpBytes, the page
    // reloads instead.
    [Fact]
    public async Task A_catch_up_past_its_byte_budget_starts_again_from_the_last_seq()
    {
        var big = new string('x', 3 * 1024 * 1024);
        for (var i = 0; i < 3; i++)
        {
            var batch = new RowBatch();
            batch.Rows.Add(new NewRow("run-1", null, RowKind.User, JsonSerializer.SerializeToElement(new { text = big }), 1));
            _hub.Publish("s", batch);
        }
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"rows","key":"s","after":0}""");
        Assert.Equal("""{"t":"state","sub":1,"seq":3,"items":[]}""", await ReadAsync(socket));
        Commit("s", 1);
        AssertRow(await ReadAsync(socket), sub: 1, seq: 4, prevSeq: 3);
    }

    // The window's reconnect after the backend drops its socket: it subscribes again with after = the last seq it
    // applied, and gets the rows saved while it was away, once each, then the new ones.
    [Fact]
    public async Task A_reconnect_after_a_drop_gets_only_the_rows_it_missed()
    {
        Commit("s", 2);
        using (var socket = await OpenAsync())
        {
            await SendAsync(socket, """{"t":"sub","id":1,"topic":"rows","key":"s"}""");
            Assert.Equal("""{"t":"state","sub":1,"seq":2,"items":[]}""", await ReadAsync(socket));
            _endpoint.DropAll();
            await SocketTests.AssertEndsAsync(socket);
        }

        Commit("s", 2);
        using var again = await OpenAsync();
        await SendAsync(again, """{"t":"sub","id":1,"topic":"rows","key":"s","after":2}""");
        Assert.Equal("""{"t":"state","sub":1,"seq":2,"items":[]}""", await ReadAsync(again));
        AssertRow(await ReadAsync(again), sub: 1, seq: 3, prevSeq: 2);
        AssertRow(await ReadAsync(again), sub: 1, seq: 4, prevSeq: 3);
        Commit("s", 1);
        AssertRow(await ReadAsync(again), sub: 1, seq: 5, prevSeq: 4);
    }

    [Fact]
    public async Task Another_sessions_rows_never_arrive()
    {
        using var socket = await OpenAsync();
        await SendAsync(socket, """{"t":"sub","id":1,"topic":"rows","key":"s"}""");
        await ReadAsync(socket);

        Commit("t", 2);
        Commit("s", 1);
        AssertRow(await ReadAsync(socket), sub: 1, seq: 1, prevSeq: 0);
    }

    [Fact]
    public async Task A_rows_sub_needs_a_session()
    {
        using var socket = await OpenAsync();
        await SendAsync(socket, """{"t":"sub","id":1,"topic":"rows"}""");

        Assert.Contains("\"bad_args\"", await ReadAsync(socket));
    }

    [Fact]
    public async Task Rows_page_answers_the_page_before_a_seq()
    {
        Commit("s", 5);
        var expected = _hub.Page("s", 5, 2);
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"call","id":1,"feature":"rows","method":"page","args":{"session":"s","before":5,"limit":2}}""");

        using var answer = JsonDocument.Parse(await ReadAsync(socket));
        var result = answer.RootElement.GetProperty("result");
        Assert.Equal(expected.Rows.Select(row => row.Seq), result.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("seq").GetInt64()));
        Assert.Equal(expected.HasBefore, result.GetProperty("hasBefore").GetBoolean());
        Assert.Equal(expected.Before, result.GetProperty("before").GetInt64());
    }

    [Fact]
    public async Task Rows_page_reads_at_most_a_page()
    {
        Commit("s", RowsFeature.PageLimit + 1);
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"call","id":1,"feature":"rows","method":"page","args":{"session":"s","before":999999,"limit":100000}}""");

        using var answer = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal(RowsFeature.PageLimit, answer.RootElement.GetProperty("result").GetProperty("rows").GetArrayLength());
    }

    // A closed tab really stops: the catch-up rows still queued for a sub it drops never go out.
    [Fact]
    public async Task An_unsub_drops_the_rows_still_queued_for_it()
    {
        Commit("s", 10);
        var socket = new HeldSocket();
        var connection = new SocketConnection(socket, _calls, TimeProvider.System);
        socket.Receive("""{"t":"hello","token":"held","protocol":1}""");
        var running = connection.RunAsync(Encoding.UTF8.GetBytes("held"), "test-machine", CancellationToken.None);
        Assert.StartsWith("""{"t":"welcome""", await socket.SentAsync());

        // The state's send holds, with the ten rows queued behind it, until the unsub has been read.
        socket.Receive("""{"t":"sub","id":1,"topic":"rows","key":"s","after":0}""");
        await Until(() => socket.Sends >= 2);
        socket.Receive("""{"t":"unsub","id":1}""");
        await Until(() => socket.Receives >= 4);
        socket.Release();
        socket.Receive("""{"t":"call","id":2,"feature":"nothing","method":"here","args":{}}""");

        Assert.Equal("""{"t":"state","sub":1,"seq":0,"items":[]}""", await socket.SentAsync());
        Assert.Contains("\"not_found\"", await socket.SentAsync());
        socket.Close();
        await running.WaitAsync(Wait);
    }

    private void Commit(string session, int rows)
    {
        var batch = new RowBatch();
        for (var i = 0; i < rows; i++)
            batch.Rows.Add(new NewRow("run-1", null, RowKind.User, JsonSerializer.SerializeToElement(new { text = $"row {i}" }), 1));
        _hub.Publish(session, batch);
    }

    private static void AssertRow(string frame, long sub, long seq, long prevSeq)
    {
        using var json = JsonDocument.Parse(frame);
        Assert.Equal("rows", json.RootElement.GetProperty("t").GetString());
        Assert.Equal(sub, json.RootElement.GetProperty("sub").GetInt64());
        var row = json.RootElement.GetProperty("row");
        Assert.Equal(seq, row.GetProperty("seq").GetInt64());
        Assert.Equal(prevSeq, row.GetProperty("prevSeq").GetInt64());
    }

    private static async Task Until(Func<bool> done)
    {
        for (var waited = TimeSpan.Zero; !done(); waited += TimeSpan.FromMilliseconds(10))
        {
            Assert.True(waited < Wait, "it didn't happen within 10 s");
            await Task.Delay(10);
        }
    }

    // A resume doesn't call rows/page, so the sub itself starts the remote session's follower, as a page call does;
    // without it, rows the remote ran meanwhile would never be copied in.
    [Fact]
    public async Task A_resume_sub_on_a_remote_session_has_its_machine_watch_it()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        using var socket = await OpenAsync();

        await SendAsync(socket, $$"""{"t":"sub","id":1,"topic":"rows","key":"{{session.SessionId}}","after":0}""");
        await ReadAsync(socket);

        Assert.True(SpinWait.SpinUntil(() => _box.Asked.Contains($"watch {session.SessionId}"), Wait));
    }

    private async Task<ClientWebSocket> OpenAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"rows-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReadAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    // A socket the test feeds frames to, whose sends after the welcome wait until it's released.
    private sealed class HeldSocket : WebSocket
    {
        private readonly Channel<string> _in = Channel.CreateUnbounded<string>();
        private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sends;
        private int _receives;

        public int Receives => Volatile.Read(ref _receives);
        public int Sends => Volatile.Read(ref _sends);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public void Receive(string text) => _in.Writer.TryWrite(text);
        public void Release() => _released.TrySetResult();
        public void Close() => _in.Writer.TryComplete();
        public async Task<string> SentAsync() => await _sent.Reader.ReadAsync().AsTask().WaitAsync(Wait);

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _receives);
            if (!await _in.Reader.WaitToReadAsync(cancellationToken) || !_in.Reader.TryRead(out var text))
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            var count = Encoding.UTF8.GetBytes(text, buffer);
            return new WebSocketReceiveResult(count, WebSocketMessageType.Text, true);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _sends) > 1)
                await _released.Task;
            _sent.Writer.TryWrite(Encoding.UTF8.GetString(buffer));
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Abort() => _in.Writer.TryComplete();
        public override void Dispose() { }
    }
}
