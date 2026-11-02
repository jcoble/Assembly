using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class SessionTitleSnapshotTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Actual_connection_with_paused_sender_queues_six_large_titles_as_one_bounded_complete_state()
    {
        var topics = new TopicSet(); using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, Path.GetTempPath());
        var ids = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var session = sessions.Create("codex", "repo"); ids.Add(session.SessionId);
            sessions.SetTitle(session.SessionId, new string('x', 3 * 1024 * 1024) + i, "prompt");
        }
        var expectedOrder = sessions.List(null, SessionsFeature.ListLimit, settled: false).Sessions.Select(session => session.SessionId).ToArray();
        var calls = new SocketDispatcher(f.Db.Writer, topics, f.Time, NullLogger.Instance);
        new SessionsFeature(sessions, new MachineSet(new FakeMachine(null, sessions))).Register(calls);
        using var socket = new PausedSender();
        socket.Feed("""{"t":"hello","protocol":1,"token":"test"}""");
        socket.Feed("""{"t":"sub","id":9223372036854775807,"topic":"sessions","sessionTitleParts":1}""");
        var connection = new SocketConnection(socket, calls, f.Time);
        var running = connection.RunAsync(Encoding.UTF8.GetBytes("test"), f.Machine, default, f.Machine);
        try
        {
            var frame = await socket.StateEntered.Task.WaitAsync(Wait);
            Assert.True(frame.Length <= SocketConnection.MaxFrame);
            Assert.False(socket.Aborted);
            Assert.Equal(2, socket.Sends); // Welcome and exactly one state, held inside the actual sender.
            using var parsed = JsonDocument.Parse(frame);
            Assert.Equal("state", parsed.RootElement.GetProperty("t").GetString());
            Assert.Equal(1, parsed.RootElement.GetProperty("sessionTitleParts").GetInt32());
            var items = parsed.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(6, items.Length);
            Assert.Equal(expectedOrder, items.Select(entry => entry.GetProperty("key").GetString()!));
            Assert.Equal(5, items.Count(entry => entry.GetProperty("item").TryGetProperty("sessionTitleRefVersion", out _)));
            Assert.All(ids, id => Assert.Equal(3 * 1024 * 1024 + 1, sessions.Get(id)!.Title!.Length));
            f.Time.Advance(TimeSpan.FromMilliseconds(1)); sessions.Touch(ids[0]);
            Assert.False(socket.Aborted);
            socket.Release.TrySetResult();
            using var first = JsonDocument.Parse(await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(Wait));
            using var next = JsonDocument.Parse(await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(Wait));
            Assert.Equal("state", first.RootElement.GetProperty("t").GetString());
            Assert.Equal("change", next.RootElement.GetProperty("t").GetString());
            Assert.Equal(ids[0], next.RootElement.GetProperty("key").GetString());
            Assert.Equal(sessions.Get(ids[0])!.UpdatedAt, next.RootElement.GetProperty("item").GetProperty("updatedAt").GetInt64());
        }
        finally { socket.Release.TrySetResult(); socket.End(); await running.WaitAsync(Wait); }
        Assert.False(socket.Aborted); Assert.Equal(3, socket.Sends);
    }

    [Fact]
    public async Task Truly_overfull_later_traffic_still_hits_the_unchanged_slow_consumer_limit()
    {
        var topics = new TopicSet(); using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, Path.GetTempPath());
        var ids = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var session = sessions.Create("codex", "repo"); ids.Add(session.SessionId);
            sessions.SetTitle(session.SessionId, new string('x', 3 * 1024 * 1024) + i, "prompt");
        }
        var calls = new SocketDispatcher(f.Db.Writer, topics, f.Time, NullLogger.Instance);
        new SessionsFeature(sessions, new MachineSet(new FakeMachine(null, sessions))).Register(calls);
        using var socket = new PausedSender();
        socket.Feed("""{"t":"hello","protocol":1,"token":"test"}""");
        socket.Feed("""{"t":"sub","id":1,"topic":"sessions","sessionTitleParts":1}""");
        var running = new SocketConnection(socket, calls, f.Time).RunAsync(Encoding.UTF8.GetBytes("test"), f.Machine, default, f.Machine);
        try
        {
            await socket.StateEntered.Task.WaitAsync(Wait);
            Assert.False(socket.Aborted);
            foreach (var id in ids) sessions.Touch(id);
            Assert.True(socket.Aborted);
        }
        finally { socket.Release.TrySetResult(); socket.End(); await running.WaitAsync(Wait); }
    }

    [Theory]
    [InlineData("{\"t\":\"sub\",\"id\":1,\"topic\":\"notes\",\"sessionTitleParts\":1}")]
    [InlineData("{\"t\":\"sub\",\"id\":1,\"topic\":\"sessions\",\"sessionTitleParts\":2}")]
    [InlineData("{\"t\":\"sub\",\"id\":1,\"topic\":\"sessions\",\"stream\":\"engine\",\"sessionTitleParts\":1}")]
    public async Task Title_parts_opt_in_rejects_other_topics_versions_and_streams(string subscription)
    {
        var topics = new TopicSet(); using var f = new EngineFixture(topics: topics);
        var calls = new SocketDispatcher(f.Db.Writer, topics, f.Time, NullLogger.Instance);
        using var socket = new PausedSender();
        socket.Feed("""{"t":"hello","protocol":1,"token":"test"}"""); socket.Feed(subscription);
        var running = new SocketConnection(socket, calls, f.Time).RunAsync(Encoding.UTF8.GetBytes("test"), f.Machine, default, f.Machine);
        try
        {
            using var parsed = JsonDocument.Parse(await socket.StateEntered.Task.WaitAsync(Wait));
            Assert.Equal("error", parsed.RootElement.GetProperty("t").GetString());
            Assert.Equal("bad_args", parsed.RootElement.GetProperty("code").GetString());
        }
        finally { socket.Release.TrySetResult(); socket.End(); await running.WaitAsync(Wait); }
    }

    [Fact]
    public async Task Ordinary_small_sessions_subscription_retains_its_original_generated_item_and_has_no_opt_in_ack()
    {
        var topics = new TopicSet(); using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, Path.GetTempPath());
        var session = sessions.Create("codex", "repo"); sessions.SetTitle(session.SessionId, "Exact\0世界😀", "prompt");
        using var expectedBytes = new MemoryStream();
        using (var json = new Utf8JsonWriter(expectedBytes, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            JsonSerializer.Serialize(json, sessions.Item(sessions.Get(session.SessionId)!), SessionsJson.Default.SessionItem);
        var expected = Encoding.UTF8.GetString(expectedBytes.ToArray());
        var calls = new SocketDispatcher(f.Db.Writer, topics, f.Time, NullLogger.Instance);
        new SessionsFeature(sessions, new MachineSet(new FakeMachine(null, sessions))).Register(calls);
        using var socket = new PausedSender();
        socket.Feed("""{"t":"hello","protocol":1,"token":"test"}"""); socket.Feed("""{"t":"sub","id":1,"topic":"sessions"}""");
        var running = new SocketConnection(socket, calls, f.Time).RunAsync(Encoding.UTF8.GetBytes("test"), f.Machine, default, f.Machine);
        try
        {
            using var parsed = JsonDocument.Parse(await socket.StateEntered.Task.WaitAsync(Wait));
            Assert.False(parsed.RootElement.TryGetProperty("sessionTitleParts", out _));
            Assert.Equal(expected, parsed.RootElement.GetProperty("items")[0].GetProperty("item").GetRawText());
        }
        finally { socket.Release.TrySetResult(); socket.End(); await running.WaitAsync(Wait); }
    }

    private sealed class PausedSender : WebSocket
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private int _sends;
        internal int Sends => Volatile.Read(ref _sends);
        internal Channel<byte[]> Sent { get; } = Channel.CreateUnbounded<byte[]>();
        internal bool Aborted { get; private set; }
        internal TaskCompletionSource<byte[]> StateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Feed(string text) => _incoming.Writer.TryWrite(text);
        internal void End() => _incoming.Writer.TryComplete();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { Aborted = true; End(); Release.TrySetResult(); }
        public override void Dispose() { End(); Release.TrySetResult(); }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (!await _incoming.Reader.WaitToReadAsync(token) || !_incoming.Reader.TryRead(out var text)) return new(0, WebSocketMessageType.Close, true);
            var count = Encoding.UTF8.GetBytes(text, buffer); return new(count, WebSocketMessageType.Text, true);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            if (Interlocked.Increment(ref _sends) == 1) return;
            StateEntered.TrySetResult(buffer.ToArray()); await Release.Task.WaitAsync(token);
            if (Aborted) throw new WebSocketException("aborted slow consumer");
            Sent.Writer.TryWrite(buffer.ToArray());
        }
    }
}
