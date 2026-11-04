using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Infrastructure.Socket;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class GitCheckoutScopeTests
{
    [Fact]
    public async Task Queued_checkout_reply_refuses_changed_recorded_root_or_source()
    {
        await using (var f = await Fixture.Start())
        {
            foreach (var invalid in new string?[] { null, "", "bad\0root", "relative" })
                await QueuedRefusal(f, () => { f.SetRoot(invalid); return Task.CompletedTask; });
        }
        await using (var remote = await Fixture.Start(remote: true))
            await QueuedRefusal(remote, remote.Hosts.DisconnectMirror);
    }

    private static async Task QueuedRefusal(Fixture f, Func<Task> invalidate)
    {
        f.SetRoot(f.Root);
        using var socket = new PausedSocket();
        socket.Feed("{\"t\":\"hello\",\"token\":\"isolated-token\",\"protocol\":1}");
        var connection = new SocketConnection(socket, f.Dispatcher, TimeProvider.System);
        var running = connection.RunAsync(Encoding.UTF8.GetBytes("isolated-token"), f.Hosts.Mac.Fixture.Machine, default, f.Hosts.Mac.Fixture.Machine);
        try
        {
            await socket.Take(); // Actual authenticated welcome establishes OwnerPeer.
            socket.Hold = true;
            connection.Send(Encoding.UTF8.GetBytes("{\"t\":\"result\",\"id\":-1,\"result\":null}"));
            await socket.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            // Run the actual registered dispatcher/route to completion, retain its actual guard, then queue behind
            // the held send. This avoids timing assumptions about the tiny dispatcher-to-queue scheduling interval.
            var frame = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { t = "call", id = 77, feature = "git", method = "status", args = f.Args(selected: true) }, Wire), SocketJson.Default.InFrame)!;
            var answer = await f.Dispatcher.AnswerAsync(frame, 77, connection, default);
            using (var parsed = JsonDocument.Parse(answer)) Assert.Equal("result", parsed.RootElement.GetProperty("t").GetString());
            var guard = Assert.IsType<GuardLease>(f.Captured);
            connection.Send(answer, new SourceBoundReply(77, guard));
            await invalidate(); socket.Release.TrySetResult();
            await socket.Take(); // The unrelated held result.
            AssertCode(await socket.Take(), "source_changed");
            Assert.True(guard.Disposed);
            Assert.Equal(WebSocketState.Open, socket.State);
            f.SetRoot(f.Root);
            socket.Feed(JsonSerializer.Serialize(new { t = "call", id = 78, feature = "git", method = "branches", args = f.Args() }, Wire));
            var next = await socket.Take();
            Assert.Equal(78, next.GetProperty("id").GetInt64());
            Assert.Equal(WebSocketState.Open, socket.State);
        }
        finally { socket.Release.TrySetResult(); socket.End(); await running.WaitAsync(TimeSpan.FromSeconds(20)); }
    }

    private sealed class PausedSocket : WebSocket
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private readonly Channel<byte[]> _sent = Channel.CreateUnbounded<byte[]>();
        private bool _aborted;
        internal bool Hold;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Feed(string text) => _incoming.Writer.TryWrite(text);
        internal void End() => _incoming.Writer.TryComplete();
        internal async Task<JsonElement> Take()
        {
            using var json = JsonDocument.Parse(await _sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)));
            return json.RootElement.Clone();
        }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { _aborted = true; End(); Release.TrySetResult(); }
        public override void Dispose() { End(); Release.TrySetResult(); }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> bytes, CancellationToken ct)
        {
            if (!await _incoming.Reader.WaitToReadAsync(ct) || !_incoming.Reader.TryRead(out var text)) return new(0, WebSocketMessageType.Close, true);
            var count = Encoding.UTF8.GetBytes(text, bytes); return new(count, WebSocketMessageType.Text, true);
        }
        public override async Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            if (Hold) { Hold = false; Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            _sent.Writer.TryWrite(bytes.ToArray());
        }
    }
}
