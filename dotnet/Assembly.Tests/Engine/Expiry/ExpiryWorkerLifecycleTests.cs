using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Preferences;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Hosting;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Expiry;

public sealed partial class ExpiryIntegrationTests
{
    [Fact]
    public async Task New_current_writer_policy_push_interrupts_long_slice_and_disposal_cancels_its_owned_timer()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); f.SeedSession(state: "running"); _ = new PreferenceStore(f.Db);
        f.Db.Writer.Write("test.setting", s => s.Execute("INSERT INTO preferences VALUES('messageExpiryMinutes','72000')"));
        var clock = new TimerClock(f.Time); using var retention = new Passes(topics);
        var runtime = EngineRuntime.Start(f.Db, topics, clock, f.Limits, retention: retention, readExpiryMinutes: MessageExpiryPreferences.ReadMinutes);
        try
        {
            await retention.Next(); Assert.False(clock.Created.Reader.TryRead(out _));
            Write(runtime, f, "long", "interrupt"); Assert.Equal(4294967294L, (await Timer(clock)).TotalMilliseconds);
            ((IEngineRuntime)runtime).Write(s => { s.Execute("UPDATE preferences SET value='1' WHERE key='messageExpiryMinutes'"); return runtime.Commands.Apply(s, Context(f, "short"), new SendMessage(null, "s", "short", "prompt")); });
            await TimerUntil(clock, 60000); Assert.InRange(clock.Active, 0, 1);
            f.Time.Advance(TimeSpan.FromMinutes(1)); await retention.NextState(f, "short", "failed");
            Assert.Equal("held", f.State("engine_message", "long"));
            await TimerUntil(clock, 4294967294L); Assert.InRange(clock.Active, 0, 1);
        }
        finally { await runtime.DisposeAsync(); }
        Assert.Equal(0, clock.Active);
    }

    [Fact]
    public async Task Active_completion_interrupts_long_slice_and_recomputes_its_owned_wait()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); f.SeedSession(state: "running"); f.SeedSession("stopping", "running");
        Assert.True(f.Apply(new SendMessage(null, "s", "pending", "prompt"), "m").Applied);
        f.Db.Writer.Write("test.long-deadline", s => s.Execute("UPDATE engine_message SET expires_ms=4320001000 WHERE id='m'"));
        Assert.True(f.Apply(new Assembly.Engine.Sessions.StopSession(), "stopping").Applied);
        var finish = new TaskCompletionSource<EffectReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = new FakeEffects { Execute = (_, ct) => finish.Task.WaitAsync(ct) };
        var clock = new TimerClock(f.Time); using var retention = new Passes(topics);
        await using var runtime = EngineRuntime.Start(f.Db, topics, clock, f.Limits, effects, retention: retention);
        await effects.FirstAttempt.WaitAsync(Timeout); await TimerUntil(clock, 4294967294L);
        finish.SetResult(new(EffectDisposition.Confirmed, System.Text.Json.JsonSerializer.SerializeToElement(new SessionEffectData("idle", null, null), global::Assembly.Engine.EngineJson.Default.SessionEffectData), null));
        while (f.Text("SELECT state FROM engine_outbox WHERE kind='stop'") != "done") await retention.Next();
        await TimerUntil(clock, 4294967294L);
        Assert.Equal("queued", f.State("engine_message", "m"));
        Assert.Equal(4320001000L, f.Number("SELECT expires_ms FROM engine_message WHERE id='m'"));
        Assert.Single(effects.ExecutedIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utc_jumps_recompute_on_push_without_rewriting_the_deadline(bool forward)
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); f.SeedSession(state: "running");
        Assert.True(f.Apply(new SendMessage(null, "s", "held", "interrupt"), "m").Applied);
        var clock = new TimerClock(f.Time); using var retention = new Passes(topics);
        await using var runtime = EngineRuntime.Start(f.Db, topics, clock, f.Limits, retention: retention);
        Assert.Equal(1800000, (await Timer(clock)).TotalMilliseconds);
        clock.Offset = forward ? 1800000 : -60000; ((IEngineRuntime)runtime).Write(_ => 0);
        if (forward) await retention.NextState(f, "m", "failed");
        else
        {
            await TimerUntil(clock, 1860000);
            f.Time.Advance(TimeSpan.FromMinutes(30)); Assert.Equal("held", f.State("engine_message", "m"));
            f.Time.Advance(TimeSpan.FromMinutes(1)); await retention.NextState(f, "m", "failed");
        }
        Assert.Equal(1801000, f.Number("SELECT expires_ms FROM engine_message WHERE id='m'"));
    }

    [Fact]
    public async Task More_than_one_hundred_due_messages_continue_in_bounded_passes_then_wait_without_idle_timer()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); f.SeedSession(state: "running");
        for (var i = 0; i < 205; i++) Assert.True(f.Apply(new SendMessage(null, "s", "body", "prompt"), "m" + i).Applied);
        f.Time.Advance(TimeSpan.FromMinutes(30)); using var retention = new Passes(topics); var clock = new TimerClock(f.Time);
        await using var runtime = EngineRuntime.Start(f.Db, topics, clock, f.Limits, retention: retention);
        await retention.NextState(f, "m204", "failed");
        Assert.Equal(205, f.Number("SELECT COUNT(*) FROM engine_message WHERE state='failed' AND reason='expired'"));
        Assert.Equal(205, f.Number("SELECT COUNT(*) FROM engine_event WHERE kind='message.changed' AND json_extract(data_json,'$.state')='failed'"));
        Assert.False(clock.Created.Reader.TryRead(out _)); Assert.InRange(retention.Count, 1, 6);
    }

    [Fact]
    public async Task Uncertain_due_reservation_has_no_expiry_timer_or_resend_and_disposes_idle_owner()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); f.SeedSession();
        Assert.True(f.Apply(new SendMessage(null, "s", "body", "prompt"), "m").Applied);
        Assert.True(f.Apply(new ClaimEffect(), "send/m/1", f.Number("SELECT version FROM engine_outbox"), actor: new(ActorKind.System, "worker")).Applied);
        var unknown = new EffectReceipt(EffectDisposition.Unknown, System.Text.Json.JsonSerializer.SerializeToElement((string?)null), "interrupted");
        Assert.True(f.Apply(new CompleteEffect(unknown), "send/m/1", f.Number("SELECT version FROM engine_outbox"), actor: new(ActorKind.System, "worker")).Applied);
        f.Time.Advance(TimeSpan.FromMinutes(31)); var clock = new TimerClock(f.Time); using var retention = new Passes(topics);
        var runtime = EngineRuntime.Start(f.Db, topics, clock, f.Limits, retention: retention);
        await retention.Next(); ((IEngineRuntime)runtime).Write(_ => 0); await retention.Next();
        Assert.Equal("queued", f.State("engine_message", "m")); Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message"));
        Assert.Equal(1, f.Number("SELECT attempt FROM engine_outbox")); Assert.False(clock.Created.Reader.TryRead(out _));
        await runtime.DisposeAsync(); Assert.Equal(0, clock.Active); Assert.InRange(retention.Count, 1, 6);
    }

    private static CommandContext Context(EngineFixture f, string id) => new(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), id, 0, 0, f.Time.GetUtcNow().ToUnixTimeMilliseconds());
    private static void Write(EngineRuntime runtime, EngineFixture f, string id, string mode) => Assert.True(((IEngineRuntime)runtime).Write(s => runtime.Commands.Apply(s, Context(f, id), new SendMessage(null, "s", id, mode))).Applied);
    private static async Task TimerAt(TimerClock clock, long milliseconds, long createdAt)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var timer = await clock.Created.Reader.ReadAsync(timeout.Token);
            if (timer.At == createdAt && timer.Due.TotalMilliseconds == milliseconds) return;
        }
    }
    private static async Task TimerUntil(TimerClock clock, long milliseconds)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while ((await clock.Created.Reader.ReadAsync(timeout.Token)).Due.TotalMilliseconds != milliseconds) { }
    }
    private static async Task<TimeSpan> Timer(TimerClock clock) => (await clock.Created.Reader.ReadAsync().AsTask().WaitAsync(Timeout)).Due;
    private sealed class Passes : IEngineRetention, IDisposable
    {
        private int _count;
        private readonly Channel<bool> _passes = Channel.CreateUnbounded<bool>();
        private readonly IDisposable _subscription;
        public Passes(Topics topics) { _subscription = topics.Subscribe("test.expiry.pass", null, _ => { }, (_, _) => _passes.Writer.TryWrite(true)); }
        public int Count => Volatile.Read(ref _count);
        public long? NextDeadlineMs(IReadScope scope, long now) => null;
        public void Apply(IWriteScope scope, long now) { var count = (long)Interlocked.Increment(ref _count); scope.Publish("test.expiry.pass", null, count, global::Assembly.Engine.EngineJson.Default.Int64); }
        public void Dispose() => _subscription.Dispose();
        public async Task Next() => await _passes.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        public async Task NextState(EngineFixture f, string id, string state) { while (f.State("engine_message", id) != state) await Next(); }
    }
}
