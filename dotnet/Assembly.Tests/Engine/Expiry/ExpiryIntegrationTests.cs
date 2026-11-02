using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Engine;
using Microsoft.Extensions.Time.Testing;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Expiry;

public sealed partial class ExpiryIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData("none")]
    [InlineData("need")]
    [InlineData("lost")]
    public void Current_known_notinserted_due_busy_expires_without_depending_on_a_release_trigger(string blocker)
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(f.Apply(new SendMessage(null, "s", "body", "prompt"), "m").Applied);
        const string effect = "send/m/1";
        Assert.True(f.Apply(new ClaimEffect(), effect, f.Number("SELECT version FROM engine_outbox WHERE id='send/m/1'"), actor: new(ActorKind.System, "worker")).Applied);
        if (blocker == "need") Assert.True(f.Apply(new Assembly.Engine.NeedsMe.OpenNeed("s", null, null, "question", "Waiting", ["yes", "no"], "provider-request", null, null), "need", actor: new(ActorKind.System, "observer")).Applied);
        if (blocker == "lost") Assert.True(f.Apply(new Assembly.Engine.Sessions.SetSessionState("lost", null, "test"), "s", f.Number("SELECT version FROM engine_session WHERE id='s'"), actor: new(ActorKind.System, "observer")).Applied);
        f.Time.Advance(TimeSpan.FromMinutes(30));
        var receipt = new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new MessageEffectData("queued", null, "busy"), global::Assembly.Engine.EngineJson.Default.MessageEffectData), null);
        Assert.True(f.Apply(new CompleteEffect(receipt), effect, f.Number("SELECT version FROM engine_outbox WHERE id='send/m/1'"), actor: new(ActorKind.System, "worker")).Applied);
        Assert.Equal("failed", f.State("engine_message", "m")); Assert.Equal("expired", f.Text("SELECT reason FROM engine_message WHERE id='m'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_event WHERE entity_id='m' AND json_extract(data_json,'$.state')='failed'"));
    }

    [Fact]
    public void A_late_previous_send_attempt_is_refused_and_journaled_before_touching_current_message()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(f.Apply(new SendMessage(null, "s", "body", "prompt"), "m").Applied);
        Assert.True(f.Apply(new ClaimEffect(), "send/m/1", f.Number("SELECT version FROM engine_outbox WHERE id='send/m/1'"), actor: new(ActorKind.System, "worker")).Applied);
        f.Db.Writer.Write("test.new-attempt", s => s.Execute("UPDATE engine_message SET attempt=2 WHERE id='m'"));
        var before = f.Number("SELECT COUNT(*) FROM engine_event");
        var receipt = new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new MessageEffectData("next_turn", "provider", null), global::Assembly.Engine.EngineJson.Default.MessageEffectData), null);
        var result = f.Apply(new CompleteEffect(receipt), "send/m/1", f.Number("SELECT version FROM engine_outbox WHERE id='send/m/1'"), actor: new(ActorKind.System, "worker"));
        Assert.False(result.Applied); Assert.Equal("stale_delivery", result.Error);
        Assert.Equal("queued", f.State("engine_message", "m")); Assert.Equal("inflight", f.State("engine_outbox", "send/m/1")); Assert.Equal(before, f.Number("SELECT COUNT(*) FROM engine_event"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE json_extract(result_json,'$.error')='stale_delivery'"));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(72000)]
    [InlineData(int.MaxValue)]
    public async Task Actual_worker_owns_range_safe_expiry_wait_and_expires_at_persisted_utc_deadline(int minutes)
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); f.SeedSession(state: "running");
        Assert.True(f.Apply(new SendMessage(null, "s", "held", "interrupt"), "m").Applied);
        var duration = (long)minutes * 60000; var deadline = 1000 + duration;
        f.Db.Writer.Write("test.wait-boundary", s => s.Execute("UPDATE engine_message SET expires_ms=$deadline WHERE id='m'", new SqlArg("$deadline", deadline)));
        var clock = new TimerClock(f.Time); using var retention = new Passes(topics);
        await using var runtime = EngineRuntime.Start(f.Db, topics, clock, f.Limits, retention: retention);
        await TimerAt(clock, Math.Min(duration, 4294967294L), f.Time.GetUtcNow().ToUnixTimeMilliseconds());
        if (duration > 4294967294L)
        {
            f.Time.Advance(TimeSpan.FromMilliseconds(4294967294L));
            await TimerAt(clock, Math.Min(duration - 4294967294L, 4294967294L), f.Time.GetUtcNow().ToUnixTimeMilliseconds());
            Assert.Equal("held", f.State("engine_message", "m"));
        }
        var remaining = deadline - f.Time.GetUtcNow().ToUnixTimeMilliseconds();
        f.Time.Advance(TimeSpan.FromMilliseconds(remaining));
        await retention.NextState(f, "m", "failed");
        Assert.Equal("expired", f.Text("SELECT reason FROM engine_message WHERE id='m'"));
    }

    private sealed class TimerClock(FakeTimeProvider time) : TimeProvider
    {
        private int _active;
        private long _offset;
        public int Active => Volatile.Read(ref _active);
        public long Offset { get => Interlocked.Read(ref _offset); set => Interlocked.Exchange(ref _offset, value); }
        public Channel<(TimeSpan Due, long At)> Created { get; } = Channel.CreateUnbounded<(TimeSpan, long)>();
        public override DateTimeOffset GetUtcNow() => time.GetUtcNow().AddMilliseconds(Offset);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _active);
            var timer = new OwnedTimer(time.CreateTimer(callback, state, dueTime, period), () => Interlocked.Decrement(ref _active));
            Created.Writer.TryWrite((dueTime, GetUtcNow().ToUnixTimeMilliseconds())); return timer;
        }
        private sealed class OwnedTimer(ITimer timer, Action stopped) : ITimer
        {
            private int _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { timer.Dispose(); stopped(); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
