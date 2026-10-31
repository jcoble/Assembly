using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Effects;
using Assembly.Application.Features.Engine.Handoffs;
using Assembly.Application.Features.Engine.Messages;
using Assembly.Application.Features.Engine.NeedsMe;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Sessions;
using Assembly.Application.Features.Engine.Runs;
using Assembly.Infrastructure.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine;

public sealed class EffectTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static EffectReceipt Receipt(EffectDisposition disposition) => new(disposition, JsonSerializer.SerializeToElement(new AnswerEffectData("request-7"), EngineJson.Default.AnswerEffectData), null);

    private static void Answer(EngineFixture f)
    {
        f.SeedSession();
        Assert.True(f.Apply(new OpenNeed("s", null, null, "question", "Continue?", [], "request-7", null, null), "n",
            actor: new(ActorKind.System, "test")).Applied);
        Assert.True(f.Apply(new ResolveNeed("answered", JsonSerializer.SerializeToElement("yes")), "n", 1).Applied);
    }

    private static async Task Until(Channel<EngineEvent> events, Func<EngineEvent, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!predicate(await events.Reader.ReadAsync(timeout.Token))) { }
    }

    [Fact]
    public async Task Retry_preserves_answer_and_effect_id()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        var fake = new FakeEffects { Execute = (e, _) => Task.FromResult(Receipt(e.Attempt == 1 ? EffectDisposition.TransientFailure : EffectDisposition.Confirmed)) };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        Answer(f);
        await fake.FirstAttempt.WaitAsync(Timeout);
        await Until(events, e => e.EntityId == "answer/n" && e.Data.State == "pending");
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await fake.SecondAttempt.WaitAsync(Timeout);
        await Until(events, e => e.EntityId == "answer/n" && e.Data.State == "done");
        Assert.Equal(fake.Attempts[0].Id, fake.Attempts[1].Id);
        Assert.Equal("answered", f.State("engine_needs_me", "n"));
        Assert.Equal(new[] { "request-7" }, fake.RequestIds);
    }

    [Fact]
    public async Task Crash_after_external_success_reconciles()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeEffects fake = null!;
        fake = new FakeEffects
        {
            Execute = async (e, ct) =>
        {
            fake.SucceededExternally(e);
            try { await Task.Delay(System.Threading.Timeout.Infinite, ct); }
            finally { joined.TrySetResult(); }
            return Receipt(EffectDisposition.Confirmed);
        }
        };
        var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        Answer(f);
        await fake.ExternalSuccess.WaitAsync(Timeout);
        await runtime.DisposeAsync();
        Assert.True(joined.Task.IsCompletedSuccessfully);
        Assert.Equal("inflight", f.State("engine_outbox", "answer/n"));
        f.Reopen();
        var restarted = new FakeEffects { Execute = (_, _) => throw new Xunit.Sdk.XunitException("Replayed uncertain effect"), Reconcile = (_, _) => Task.FromResult(Receipt(EffectDisposition.Confirmed)) };
        await using var next = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, restarted);
        var recoveredEvents = Channel.CreateUnbounded<EngineEvent>();
        using var recoveredConsumer = next.Publisher.Register(e => recoveredEvents.Writer.TryWrite(e), out _);
        await restarted.Reconciled.WaitAsync(Timeout);
        if (f.State("engine_outbox", "answer/n") != "done")
            await Until(recoveredEvents, e => e.EntityId == "answer/n" && e.Data.State == "done");
        Assert.Single(fake.ExecutedIds);
        Assert.Equal(fake.ExecutedIds[0], Assert.Single(restarted.ReconciledIds));
        Assert.Empty(restarted.ExecutedIds);
    }

    [Fact]
    public async Task Publication_and_lifetime_are_owned()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        Assert.Throws<InvalidOperationException>(() => f.Db.Writer.Write<int>("rollback", s => { runtime.Commands.Apply(s, new(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "test"), "rollback", 0, 0, 1000), new StartSession("codex", "m", "/test", "title", null, null, null)); throw new InvalidOperationException(); }));
        Assert.True(f.Apply(new StartSession("codex", "m", "/test", "title", null, null, null), "s").Applied);
        await Until(events, e => { Assert.NotEqual("rollback", e.EntityId); return e.EntityId == "s"; });
        Assert.Equal(1, f.Count("engine_event")); // separate read connection sees the committed event
        Assert.Equal("pending", f.Text("SELECT state FROM engine_outbox"));
        Assert.Equal(0, f.Number("SELECT SUM(attempt) FROM engine_outbox"));
        Assert.Equal(1, runtime.Publisher.PublishedThrough);
        using var reader = runtime.Publisher.TrackReader("reader", 0, () => { });
        Assert.Equal(0, runtime.Publisher.MinimumReaderAck);
        runtime.Publisher.Acknowledge("reader", 1);
        Assert.Equal(1, runtime.Publisher.MinimumReaderAck);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Handoff_waits_for_earlier_commit_even_when_id_sorts_first()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession();
        Assert.True(f.Apply(new SendMessage(null, "s", "hello", "prompt"), "z").Applied);
        Assert.True(f.Apply(new RequestHandoff("s", "workbox-test"), "h", 0).Applied);
        var release = new TaskCompletionSource<EffectReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeEffects { Execute = (e, ct) => e.Kind == "send" ? release.Task.WaitAsync(ct) : Task.FromResult(new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new HandoffEffectData("accepted", 1, null), EngineJson.Default.HandoffEffectData), null)) };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        await fake.FirstAttempt.WaitAsync(Timeout);
        Assert.Equal("send", Assert.Single(fake.Attempts).Kind);
        Assert.Equal("pending", f.Text("SELECT state FROM engine_outbox WHERE kind='handoff'"));
        release.SetResult(new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new MessageEffectData("next_turn", null, null), EngineJson.Default.MessageEffectData), null));
        await fake.SecondAttempt.WaitAsync(Timeout);
        Assert.Equal("handoff", fake.Attempts[1].Kind);
    }

    [Fact]
    public async Task Unavailable_waits_for_explicit_availability_push()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        var fake = new FakeEffects { Execute = (e, _) => Task.FromResult(Receipt(e.Attempt == 1 ? EffectDisposition.Unavailable : EffectDisposition.Confirmed)) };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        Answer(f);
        await Until(events, e => e.EntityId == "answer/n" && e.Data.State == "unavailable");
        f.Time.Advance(TimeSpan.FromDays(10));
        Assert.Single(fake.Attempts);
        runtime.EffectsAvailable();
        await Until(events, e => e.EntityId == "answer/n" && e.Data.State == "done");
        Assert.Equal(2, fake.Attempts.Count);
    }

    [Fact]
    public async Task Unknown_is_reconciled_without_executing_twice()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        var fake = new FakeEffects { Execute = (_, _) => Task.FromResult(Receipt(EffectDisposition.Unknown)), Reconcile = (_, _) => Task.FromResult(Receipt(EffectDisposition.Confirmed)) };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        Answer(f);
        await Until(events, e => e.EntityId == "answer/n" && e.Data.State == "uncertain");
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(events, e => e.EntityId == "answer/n" && e.Data.State == "done");
        Assert.Single(fake.ExecutedIds);
        Assert.Equal("answer/n", Assert.Single(fake.ReconciledIds));
    }

    [Fact]
    public void Claim_checks_commit_order_version_due_and_system_actor()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        f.Apply(new SendMessage(null, "s", "one", "prompt"), "z");
        f.Apply(new SendMessage(null, "s", "two", "prompt"), "a");
        var system = new EngineActor(ActorKind.System, "test");
        Assert.Equal("forbidden", f.Apply(new ClaimEffect(), "send/z").Error);
        Assert.Equal("unavailable", f.Apply(new ClaimEffect(), "send/a", actor: system).Error);
        Assert.Equal("version_conflict", f.Apply(new ClaimEffect(), "send/z", 1, actor: system).Error);
        Assert.True(f.Apply(new ClaimEffect(), "send/z", actor: system).Applied);
        Assert.Equal("inflight", f.State("engine_outbox", "send/z"));
        Assert.True(f.Apply(new CompleteEffect(Receipt(EffectDisposition.TransientFailure)), "send/z", 1, actor: system).Applied);
        Assert.Equal("not_due", f.Apply(new ClaimEffect(), "send/z", 2, actor: system).Error);
        Assert.Equal("unavailable", f.Apply(new ClaimEffect(), "send/a", actor: system).Error);
    }

    [Fact]
    public void Retry_backoff_caps_at_sixty_seconds_and_failed_retains_reason()
    {
        using var f = new EngineFixture();
        Answer(f);
        var system = new EngineActor(ActorKind.System, "test");
        long version = 0;
        foreach (var delay in new long[] { 1000, 2000, 4000, 8000, 16000, 32000, 60000, 60000 })
        {
            Assert.True(f.Apply(new ClaimEffect(), "answer/n", version++, actor: system).Applied);
            Assert.True(f.Apply(new CompleteEffect(Receipt(EffectDisposition.TransientFailure)), "answer/n", version++, actor: system).Applied);
            Assert.Equal(f.Time.GetUtcNow().ToUnixTimeMilliseconds() + delay, f.Number("SELECT next_ms FROM engine_outbox WHERE id='answer/n'"));
            f.Time.Advance(TimeSpan.FromMilliseconds(delay));
        }
        Assert.True(f.Apply(new ClaimEffect(), "answer/n", version++, actor: system).Applied);
        Assert.True(f.Apply(new CompleteEffect(Receipt(EffectDisposition.Failed) with { Error = "provider refused" }), "answer/n", version, actor: system).Applied);
        Assert.Equal("failed", f.State("engine_outbox", "answer/n"));
        Assert.Equal("provider refused", f.Text("SELECT last_error FROM engine_outbox WHERE id='answer/n'"));
        Assert.Equal("answered", f.State("engine_needs_me", "n"));
    }

    [Fact]
    public async Task Interrupt_send_waits_for_stop_ack_and_stop_cancels_step_in_same_commit()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession(state: "running");
        f.SeedRun(state: "running");
        f.SeedStep(state: "running", sessionId: "s", attempt: 1);
        Assert.True(f.Apply(new SendMessage(null, "s", "interrupt", "interrupt"), "m").Applied);
        Assert.True(f.Apply(new CancelRun(), "r").Applied);
        var stopAck = new TaskCompletionSource<EffectReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeEffects { Execute = (e, ct) => e.Kind == "stop" ? stopAck.Task.WaitAsync(ct) : Task.FromResult(new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new MessageEffectData("next_turn", null, null), EngineJson.Default.MessageEffectData), null)) };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        await fake.FirstAttempt.WaitAsync(Timeout);
        Assert.Equal("stop", Assert.Single(fake.Attempts).Kind);
        Assert.Equal("held", f.State("engine_message", "m"));
        Assert.Equal("cancelling", f.State("engine_run", "r"));
        stopAck.SetResult(new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new SessionEffectData("ended", null, null), EngineJson.Default.SessionEffectData), null));
        await Until(events, e => e.EntityKind == "run" && e.Data.State == "cancelled");
        Assert.Equal("cancelled", f.State("engine_step", "step"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE run_id='r' AND state!='done'"));
    }

    [Fact]
    public async Task Need_and_owner_loss_deadlines_fire_while_handler_is_inflight()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        Answer(f);
        f.SeedRun(state: "running", ownerLossLimitMs: 2000);
        f.SeedStep(state: "running", sessionId: "s", attempt: 1);
        Assert.True(f.Apply(new OpenNeed("s", "r", null, "question", "Later?", [], null, null, 2000), "expiry", actor: new(ActorKind.System, "test")).Applied);
        var v = f.Number("SELECT version FROM engine_session WHERE id='s'");
        Assert.True(f.Apply(new SetSessionState("lost", null, null), "s", v, actor: new(ActorKind.System, "test")).Applied);
        var fake = new FakeEffects { Execute = async (_, ct) => { await Task.Delay(System.Threading.Timeout.Infinite, ct); return Receipt(EffectDisposition.Confirmed); } };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        await fake.FirstAttempt.WaitAsync(Timeout);
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(events, e => e.EntityId == "expiry" && e.Data.State == "expired");
        Assert.Equal("running", f.State("engine_run", "r"));
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(events, e => e.EntityId == "r" && e.Data.State == "failed");
        Assert.Single(fake.Attempts);
    }

    [Fact]
    public async Task Idle_stop_ack_cancels_run_after_outbox_drain()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession(state: "running");
        f.SeedRun(state: "running");
        f.SeedStep(state: "running", sessionId: "s", attempt: 1);
        Assert.True(f.Apply(new CancelRun(), "r").Applied);
        var fake = new FakeEffects { Execute = (_, _) => Task.FromResult(new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new SessionEffectData("idle", null, null), EngineJson.Default.SessionEffectData), null)) };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        if (f.Text("SELECT state FROM engine_outbox") != "done")
            await Until(events, e => e.EntityKind == "effect" && e.Data.State == "done");
        Assert.Equal("cancelled", f.State("engine_step", "step"));
        Assert.Equal("cancelled", f.State("engine_run", "r"));
    }

    [Fact]
    public async Task Publisher_catches_up_in_order_removes_throwing_consumer_and_tracks_readers()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession();
        for (var i = 0; i < 205; i++) Assert.True(f.Apply(new SendMessage(null, "s", "hello", "prompt"), $"m{i}").Applied);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out var cut);
        var calls = 0;
        using var overflow = runtime.Publisher.Register(_ => { calls++; throw new InvalidOperationException("queue full"); }, out _);
        Assert.True(f.Apply(new SendMessage(null, "s", "last", "prompt"), "last").Applied);
        var delivered = new List<long>();
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var item = await events.Reader.ReadAsync(timeout.Token);
            delivered.Add(item.Seq);
            if (item.EntityId == "last") break;
        }
        Assert.Equal(f.Events().Where(e => e.Seq > cut).Select(e => e.Seq), delivered);
        Assert.Equal(1, calls);
        Assert.Equal(runtime.Publisher.PublishedThrough, runtime.Publisher.MinimumReaderAck);
        var reset = 0;
        using var reader = runtime.Publisher.TrackReader("reader", 1, () => reset++);
        runtime.Publisher.Acknowledge("reader", 2);
        runtime.Publisher.Acknowledge("reader", 1);
        Assert.Equal(2, runtime.Publisher.MinimumReaderAck);
        runtime.Publisher.InvalidateReadersBefore(3);
        Assert.Equal(1, reset);
        Assert.Equal(runtime.Publisher.PublishedThrough, runtime.Publisher.MinimumReaderAck);
        using var replacement = runtime.Publisher.TrackReader("reader", 0, () => { });
        reader.Dispose();
        Assert.Equal(0, runtime.Publisher.MinimumReaderAck);
    }

    [Fact]
    public void Answer_receipt_must_match_the_saved_request_and_claim_receipts_are_idempotent()
    {
        using var f = new EngineFixture();
        Answer(f);
        var system = new EngineActor(ActorKind.System, "test");
        var commandId = Guid.NewGuid();
        var first = f.Apply(new ClaimEffect(), "answer/n", actor: system, commandId: commandId);
        Assert.True(first.Applied);
        Assert.Equal(first, f.Apply(new ClaimEffect(), "answer/n", actor: system, commandId: commandId));
        Assert.Equal(1, f.Number("SELECT attempt FROM engine_outbox WHERE id='answer/n'"));
        var wrong = new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new AnswerEffectData("other-request"), EngineJson.Default.AnswerEffectData), null);
        Assert.Equal("bad_receipt", f.Apply(new CompleteEffect(wrong), "answer/n", 1, actor: system).Error);
        Assert.Equal("inflight", f.State("engine_outbox", "answer/n"));
        Assert.Equal("answered", f.State("engine_needs_me", "n"));
        Assert.True(f.Apply(new CompleteEffect(Receipt(EffectDisposition.Confirmed)), "answer/n", 1, actor: system).Applied);
    }

    [Fact]
    public void Later_effect_waits_for_uncertain_earlier_one()
    {
        using var f = new EngineFixture();
        Answer(f);
        Assert.True(f.Apply(new SendMessage(null, "s", "later", "prompt"), "later").Applied);
        const string later = "send/later";
        var system = new EngineActor(ActorKind.System, "test");
        Assert.True(f.Apply(new ClaimEffect(), "answer/n", actor: system).Applied);
        Assert.True(f.Apply(new CompleteEffect(Receipt(EffectDisposition.Unknown)), "answer/n", 1, actor: system).Applied);
        Assert.Equal("uncertain", f.State("engine_outbox", "answer/n"));
        Assert.Equal("unavailable", f.Apply(new ClaimEffect(), later, actor: system).Error);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(f.Apply(new ClaimEffect(), "answer/n", 2, actor: system).Applied);
        Assert.True(f.Apply(new CompleteEffect(Receipt(EffectDisposition.Unavailable)), "answer/n", 3, actor: system).Applied);
        Assert.Equal("unavailable", f.Apply(new ClaimEffect(), later, actor: system).Error);
        Assert.True(f.Apply(new EnableEffect(), "answer/n", 4, actor: system).Applied);
        Assert.True(f.Apply(new ClaimEffect(), "answer/n", 5, actor: system).Applied);
        Assert.True(f.Apply(new CompleteEffect(Receipt(EffectDisposition.Confirmed)), "answer/n", 6, actor: system).Applied);
        Assert.Equal("done", f.State("engine_outbox", "answer/n"));
        Assert.True(f.Apply(new ClaimEffect(), later, actor: system).Applied);
    }

    [Fact]
    public async Task Disposing_duplicate_delegate_registration_preserves_the_other_registration()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5);
        var events = Channel.CreateUnbounded<EngineEvent>();
        var calls = 0;
        Action<EngineEvent> callback = e =>
        {
            Interlocked.Increment(ref calls);
            events.Writer.TryWrite(e);
        };
        using var first = runtime.Publisher.Register(callback, out _);
        using var second = runtime.Publisher.Register(callback, out _);
        Assert.True(f.Apply(new SendMessage(null, "s", "first", "prompt"), "first").Applied);
        await Until(events, e => e.EntityId == "first");
        await Until(events, e => e.EntityId == "first");
        Assert.Equal(2, Volatile.Read(ref calls));
        first.Dispose();
        Assert.True(f.Apply(new SendMessage(null, "s", "second", "prompt"), "second").Applied);
        await Until(events, e => e.EntityId == "second");
        Assert.Equal(3, Volatile.Read(ref calls));
        using var noMoreDeliveries = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await events.Reader.ReadAsync(noMoreDeliveries.Token));
    }

    [Fact]
    public void Same_commit_effects_use_insertion_order()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        var id = Guid.NewGuid();
        f.Db.Writer.Write("test.seed-effects", s => EngineJournal.Commit(s,
            new(id, f.Machine, new(ActorKind.System, "test"), "s", 0, 0, 1000), 0, 0,
            [new("session.changed", "session", "s", 0, false, new("s", null, null, null, "idle", 0))],
            [new("", "stop", "s", null, JsonSerializer.SerializeToElement(new StopSession(), EngineJson.Default.StopSession)),
             new("", "stop", "s", null, JsonSerializer.SerializeToElement(new StopSession(), EngineJson.Default.StopSession))]));
        var system = new EngineActor(ActorKind.System, "test");
        Assert.Equal("unavailable", f.Apply(new ClaimEffect(), $"{id:N}-1", actor: system).Error);
        Assert.True(f.Apply(new ClaimEffect(), $"{id:N}-0", actor: system).Applied);
        Assert.Equal("unavailable", f.Apply(new ClaimEffect(), $"{id:N}-1", actor: system).Error);
        Assert.True(f.Apply(new CompleteEffect(Receipt(EffectDisposition.Failed)), $"{id:N}-0", 1, actor: system).Applied);
        Assert.True(f.Apply(new ClaimEffect(), $"{id:N}-1", actor: system).Applied);
    }

    [Fact]
    public async Task Unavailable_reconciliation_resumes_reconciliation_after_availability_push()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        var calls = 0;
        var fake = new FakeEffects
        {
            Execute = (_, _) => Task.FromResult(Receipt(EffectDisposition.Unknown)),
            Reconcile = (_, _) => Task.FromResult(Receipt(++calls == 1 ? EffectDisposition.Unavailable : EffectDisposition.Confirmed))
        };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        Answer(f);
        await Until(events, e => e.EntityId == "answer/n" && e.Data.State == "uncertain");
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(events, e => e.EntityId == "answer/n" && e.Data.State == "unavailable");
        runtime.EffectsAvailable();
        await Until(events, e => e.EntityId == "answer/n" && e.Data.State == "done");
        Assert.Single(fake.ExecutedIds);
        Assert.Equal(2, fake.ReconciledIds.Count);
        Assert.Empty(fake.UnexpectedReplays);
    }

    [Fact]
    public async Task Confirmed_start_updates_the_saved_session_from_typed_receipt()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        var fake = new FakeEffects
        {
            Execute = (_, _) => Task.FromResult(new EffectReceipt(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new SessionEffectData("idle", "provider-session", null), EngineJson.Default.SessionEffectData), null))
        };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        Assert.True(f.Apply(new StartSession("codex", "model", "/test", "Test", null, null, null), "s").Applied);
        await Until(events, e => e.EntityKind == "effect" && e.Data.State == "done");
        Assert.Equal("idle", f.State("engine_session", "s"));
        Assert.Equal("provider-session", f.Text("SELECT provider_session_id FROM engine_session WHERE id='s'"));
    }

    [Fact]
    public async Task Invalid_answer_receipt_preserves_uncertainty_until_reconciled()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        var fake = new FakeEffects
        {
            Execute = (_, _) => Task.FromResult(new EffectReceipt(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new AnswerEffectData("wrong-request"), EngineJson.Default.AnswerEffectData), null)),
            Reconcile = (_, _) => Task.FromResult(Receipt(EffectDisposition.Confirmed))
        };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        Answer(f);
        await Until(events, e => e.EntityId == "answer/n" && e.Data.State == "uncertain");
        Assert.Equal("bad_receipt", f.Text("SELECT last_error FROM engine_outbox WHERE id='answer/n'"));
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(events, e => e.EntityId == "answer/n" && e.Data.State == "done");
        Assert.Single(fake.ExecutedIds);
        Assert.Single(fake.ReconciledIds);
        Assert.Equal("answered", f.State("engine_needs_me", "n"));
    }

    [Fact]
    public async Task Manual_stop_ack_cancels_active_step_and_attempt()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession(state: "running");
        f.SeedRun(state: "running");
        f.SeedStep(state: "running", sessionId: "s", attempt: 1);
        f.Db.Writer.Write("test.seed-attempt", s => s.Execute("INSERT INTO engine_step_attempt(step_id,attempt,state,late) VALUES('step',1,'running',0)"));
        Assert.True(f.Apply(new StopSession(), "s").Applied);
        var fake = new FakeEffects
        {
            Execute = (_, _) => Task.FromResult(new EffectReceipt(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new SessionEffectData("idle", null, null), EngineJson.Default.SessionEffectData), null))
        };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, fake);
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        if (f.Text("SELECT state FROM engine_outbox") != "done")
            if (f.Text("SELECT state FROM engine_outbox") != "done")
                await Until(events, e => e.EntityKind == "effect" && e.Data.State == "done");
        Assert.Equal("cancelled", f.State("engine_step", "step"));
        Assert.Equal("cancelled", f.Text("SELECT state FROM engine_step_attempt WHERE step_id='step' AND attempt=1"));
    }

    private sealed class Retention(long deadline) : IEngineRetention
    {
        public TaskCompletionSource Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasApplied { get; private set; }
        public int Calls { get; private set; }
        public long? NextDeadlineMs(IReadScope s, long nowMs) => HasApplied ? null : deadline;
        public void Apply(IWriteScope s, long nowMs)
        {
            Assert.Equal(deadline, nowMs);
            Assert.Equal(1, s.QueryFirst("SELECT COUNT(*) FROM engine_meta", r => r.Long(0)));
            Calls++;
            HasApplied = true;
            Applied.TrySetResult();
        }
    }

    [Fact]
    public async Task Single_deadline_wait_applies_retention_in_writer_scope()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        var retention = new Retention(2000);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, retention: retention);
        // A command publication proves the startup pass was observed without using a sleep.
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var consumer = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        Assert.True(f.Apply(new StartSession("codex", "m", "/test", "title", null, null, null), "s").Applied);
        await Until(events, e => e.EntityId == "s");
        f.Time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(retention.HasApplied);
        f.Time.Advance(TimeSpan.FromMilliseconds(1));
        await retention.Applied.Task.WaitAsync(Timeout);
        Assert.Equal(1, retention.Calls);
    }
}
