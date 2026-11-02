using System.Net.WebSockets;
using Assembly.Application.Common.Database;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class MachineDoorTests
{
    [Fact]
    public async Task Source_open_beyond_actual_socket_drain_deadline_cannot_publish_and_host_dispose_joins_it()
    {
        _f.SeedSession("sender");
        Assert.True(_f.Apply(new RegisterForeignSession("B", "codex", "foreign"), "recipient",
            actor: new(Assembly.Engine.Contracts.ActorKind.System, "machine:B")).Applied);
        // Seed through the actual checked helper with the actual owning title.
        var title = _f.Text("SELECT title FROM engine_session WHERE id='sender'")!;
        var prompt = PreparedHelperPrompt.Prepare(new("sender", "recipient", "full body", "prompt"), title, 0);
        Assert.True(_f.Db.Writer.Write("source", s => _runtime.Commands.ApplyPreparedHelperPrompt(s,
            new(Guid.NewGuid(), _runtime.Machine, new(Assembly.Engine.Contracts.ActorKind.Session, "sender"), "message", 0, 0, 1000), prompt)).Applied);
        using var socket = await OpenBulk();
        _relayDatabase.Pause = true;
        var pending = RelayCall(socket, "relay_source_open", new("message", "B"), EngineSocketJson.Default.RelaySourceReadArgs);
        try
        {
            await _relayDatabase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            socket.Abort();
            await _relayDatabase.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The read deliberately ignores cancellation until released. Drive the real socket BackgroundWork deadline.
            await _socketTime.DrainScheduled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _f.Time.Advance(TimeSpan.FromSeconds(6));
            await _bulkClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disposal = _machineCalls.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
            _relayDatabase.Release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            try { await pending; } catch (Exception error) when (socket.State == WebSocketState.Aborted && error is WebSocketException or OperationCanceledException) { }
            Assert.Equal(1, _f.Count("engine_message"));
            Assert.Equal(0, _f.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%'"));
        }
        finally { _relayDatabase.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Closing_after_writer_admission_preserves_the_queued_complete_send()
    {
        using var source = Assembly.Tests.Engine.Relays.RelaySourceTests.Source(100, out var body, out var label, _runtime.Machine);
        var descriptor = Assembly.Engine.Relays.RelayDigests.Describe(new Assembly.Engine.Reading.EngineReader(source.Db, source.Machine)
            .RelaySource(new("message", _runtime.Machine), default));
        _f.SeedSession("recipient");
        Assert.True(_f.Apply(new RegisterForeignSession(source.Machine, "codex", "stub"), "sender",
            actor: new(Assembly.Engine.Contracts.ActorKind.System, "machine:" + source.Machine)).Applied);
        using var socket = await OpenBulk(); var request = new Assembly.Engine.Relays.RelaySendRequest(descriptor, 0);
        var beginFrame = await RelayCall(socket, "relay_send_begin", request, EngineSocketJson.Default.RelaySendRequest);
        var begin = System.Text.Json.JsonSerializer.Deserialize(beginFrame.GetProperty("result"), EngineSocketJson.Default.RelaySendBeginReply)!;
        foreach (var (kind, bytes) in new[] { (Assembly.Engine.Relays.RelayPartKind.Body, System.Text.Encoding.UTF8.GetBytes(body)),
            (Assembly.Engine.Relays.RelayPartKind.SenderLabel, System.Text.Encoding.UTF8.GetBytes(label)) })
            Assert.Equal("result", (await RelayCall(socket, "relay_send_part", new(begin.Transfer!, kind, 0, bytes), EngineSocketJson.Default.RelaySendPartArgs)).GetProperty("t").GetString());
        _relayWriterPrepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _relayWriterContinue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _relayWriterClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = RelayCall(socket, "relay_send_commit", new(request, begin.Transfer), EngineSocketJson.Default.RelaySendCommitArgs);
        try
        {
            await _relayWriterPrepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
            socket.Abort(); await _relayWriterClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The stage owner can stop while the admitted writer closure still owns the complete immutable bytes.
            await _machineCalls.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, _f.Count("engine_message"));
            _relayWriterContinue.TrySetResult();
            await _bulkClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(body), _f.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id='message'", r => r.Blob(0), default));
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(label), _f.Db.QueryFirst("SELECT CAST(sender_label AS BLOB) FROM engine_message WHERE id='message'", r => r.Blob(0), default));
            Assert.Equal(1, _f.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%'"));
            try { await pending; } catch (Exception error) when (socket.State == WebSocketState.Aborted && error is WebSocketException or OperationCanceledException) { }
        }
        finally { _relayWriterContinue.TrySetResult(); }
    }

    private sealed class DeadlineTimeProvider(TimeProvider inner) : TimeProvider
    {
        internal readonly TaskCompletionSource DrainScheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(5)) DrainScheduled.TrySetResult();
            return timer;
        }
    }

    private sealed class RelayPausedDatabase(IAppDatabase inner) : IAppDatabase
    {
        internal bool Pause;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAtomicWriter Writer => inner.Writer;
        public IReadSnapshot OpenSnapshot(CancellationToken cancellationToken) => new Snapshot(this, inner.OpenSnapshot(cancellationToken), cancellationToken);
        public List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) => inner.Query(sql, map, cancel, args);
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) => inner.QueryFirst(sql, map, cancel, args);
        private sealed class Snapshot(RelayPausedDatabase owner, IReadSnapshot inner, CancellationToken token) : IReadSnapshot
        {
            public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args) { Wait(sql); return inner.Query(sql, map, args); }
            public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args) { Wait(sql); return inner.QueryFirst(sql, map, args); }
            private void Wait(string sql)
            {
                if (!owner.Pause || !sql.Contains("CAST(m.sender_label AS BLOB)", StringComparison.Ordinal)) return;
                owner.Pause = false;
                using var cancellation = token.Register(() => owner.Cancelled.TrySetResult());
                owner.Entered.TrySetResult();
                owner.Release.Task.GetAwaiter().GetResult();
            }
            public void Dispose() => inner.Dispose();
        }
    }
}
