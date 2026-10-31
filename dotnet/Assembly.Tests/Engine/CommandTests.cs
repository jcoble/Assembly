using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine;

public sealed class CommandTests
{
    private static CommandContext Context(EngineFixture f) =>
        new(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "window"), "s", 0, 0, 1000);

    // This callback stands in for a typed transition, not a production dispatch seam.
    private static CommandResult Transition(IWriteScope scope, CommandContext context, int eventCount = 1)
    {
        var current = scope.QueryFirst("SELECT version,epoch FROM engine_session WHERE id=$id",
            r => (Version: r.Long(0), Epoch: r.Long(1)), new SqlArg("$id", context.EntityId));
        if (current.Version != context.ExpectedVersion)
            return new(false, false, "version_conflict", current.Version, 0, current.Epoch);
        if (current.Epoch != context.Epoch)
            return new(false, false, "epoch_conflict", current.Version, 0, current.Epoch);
        scope.Execute("UPDATE engine_session SET state='running',version=version+1 WHERE id=$id",
            new SqlArg("$id", context.EntityId));
        var events = Enumerable.Range(0, eventCount).Select(_ => new EventDraft("session.changed", "session", "s",
            current.Version + 1, false, new("s", null, null, null, "running", current.Epoch))).ToArray();
        using var payload = JsonDocument.Parse("{}");
        return EngineJournal.Commit(scope, context, current.Version + 1, current.Epoch, events,
            [new($"{context.CommandId:N}:start", "start", "s", null, payload.RootElement)]);
    }

    private static CommandResult Execute(EngineFixture f, CommandContext context, string json = "{\"kind\":\"start\"}") =>
        f.Db.Writer.Write("test.command", scope => EngineJournal.Execute(scope, context, json,
            () => Transition(scope, context)));

    [Fact]
    public void Replay_survives_restart()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        var context = Context(f);
        var first = Execute(f, context);
        Assert.True(first.Applied);
        f.Reopen();
        var calls = 0;
        var replayAfterReopen = f.Db.Writer.Write("test.replay", scope => EngineJournal.Execute(scope,
            context with { NowMs = 9999 }, "{\"kind\":\"start\"}", () => { calls++; return Transition(scope, context); }));
        Assert.Equal(first, replayAfterReopen);
        Assert.Equal(0, calls);
        Assert.Equal(1, f.Count("engine_event"));
        Assert.Equal(1, f.Count("engine_outbox"));
        Assert.Equal(1, f.Count("engine_command_receipt"));
        Assert.Equal(1, f.Number("SELECT version FROM engine_session"));
        Assert.Equal($"{context.CommandId:N}:start", f.Text("SELECT id FROM engine_outbox"));
        var hash = f.Text("SELECT fingerprint FROM engine_command_receipt");
        Assert.Equal(64, hash!.Length);
        Assert.DoesNotContain("start", hash);
    }

    [Fact]
    public void Conflicting_id_version_or_machine_never_mutates()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        var context = Context(f);
        Execute(f, context);
        var before = f.Events();
        var changedPayload = Execute(f, context, "{\"kind\":\"stop\"}");
        var changedActor = Execute(f, context with { Actor = new(ActorKind.Session, "s") });
        var stale = Execute(f, context with { CommandId = Guid.NewGuid() });
        var wrong = Execute(f, context with { CommandId = Guid.NewGuid(), Machine = "elsewhere", ExpectedVersion = 1 });
        Assert.Equal("command_id_conflict", changedPayload.Error);
        Assert.Equal("command_id_conflict", changedActor.Error);
        Assert.Equal("version_conflict", stale.Error);
        Assert.Equal("wrong_machine", wrong.Error);
        Assert.Equal(before, f.Events());
        Assert.Equal("running", f.State("engine_session", "s"));
        Assert.Equal(1, f.Number("SELECT version FROM engine_session"));
        Assert.Equal(1, f.Count("engine_outbox"));
    }

    [Fact]
    public void Fingerprint_covers_all_checked_context_except_time()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        var c = Context(f);
        Execute(f, c);
        foreach (var changed in new[] { c with { Machine = "other" }, c with { EntityId = "other" },
                     c with { ExpectedVersion = 1 }, c with { Epoch = 1 }, c with { Actor = new(ActorKind.Window, "other") } })
            Assert.Equal("command_id_conflict", Execute(f, changed).Error);
    }

    [Fact]
    public void Invalid_actor_and_wrong_machine_do_not_run_transition()
    {
        using var f = new EngineFixture();
        var c = Context(f);
        foreach (var invalid in new[] { c with { Actor = new((ActorKind)99, "actor") },
                     c with { Actor = new(ActorKind.Window, " ") }, c with { Machine = "other" } })
        {
            var calls = 0;
            var result = f.Db.Writer.Write("test.invalid", s => EngineJournal.Execute(s,
                invalid with { CommandId = Guid.NewGuid() }, "{}", () =>
                {
                    calls++;
                    return new(true, false, null, 0, 0, 0);
                }));
            Assert.False(result.Applied);
            Assert.NotNull(result.Error);
            Assert.Equal(0, calls);
        }
        Assert.Equal(0, f.Count("engine_event"));
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Epoch_refusal_is_receipted_without_domain_writes()
    {
        using var f = new EngineFixture();
        f.SeedSession(epoch: 2);
        var c = Context(f);
        var rejected = Execute(f, c);
        Assert.Equal("epoch_conflict", rejected.Error);
        Assert.False(rejected.Applied);
        Assert.Equal(2, rejected.Epoch);
        f.Reopen();
        Assert.Equal(rejected, Execute(f, c with { NowMs = 2000 }));
        Assert.Equal((0L, 0L, 1L, 0L), Counts(f));
        Assert.Equal("idle", f.State("engine_session", "s"));
    }

    [Fact]
    public void Commit_boundary_is_atomic()
    {
        var topics = new CapturingTopics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession();
        var c = Context(f);
        var beforeNextSeq = f.Number("SELECT next_seq FROM engine_meta");
        var beforeCounts = Counts(f);
        Assert.Throws<InvalidOperationException>(() => f.Db.Writer.Write<int>("test.rollback", scope =>
        {
            EngineJournal.Execute(scope, c, "{}", () => Transition(scope, c));
            Assert.Equal(1, scope.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt", r => r.Long(0)));
            throw new InvalidOperationException("after all insertions");
        }));
        var afterRollbackNextSeq = f.Number("SELECT next_seq FROM engine_meta");
        var afterRollbackCounts = Counts(f);
        Assert.Equal(beforeNextSeq, afterRollbackNextSeq);
        Assert.Equal(beforeCounts, afterRollbackCounts);
        Assert.Equal("idle", f.State("engine_session", "s"));
        Assert.Empty(topics.Sent);
        long wakeupReaderVisibleSeq = 0;
        topics.OnPublish = () =>
        {
            // AppDatabase reads use a connection separate from AtomicWriter's writer connection.
            wakeupReaderVisibleSeq = f.Number("SELECT seq FROM engine_event ORDER BY seq DESC LIMIT 1");
            Assert.Equal(1, f.Count("engine_command_receipt"));
            Assert.Equal(1, f.Count("engine_outbox"));
        };
        var committedSeq = Execute(f, c).Seq;
        Assert.Equal(committedSeq, wakeupReaderVisibleSeq);
        Assert.Equal("__engine_committed", Assert.Single(topics.Sent));
    }

    [Theory]
    [InlineData("engine_event")]
    [InlineData("engine_outbox")]
    [InlineData("engine_command_receipt")]
    public void Insert_failure_rolls_back_rows_sequences_and_wakeups(string table)
    {
        var topics = new CapturingTopics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession();
        f.Db.Writer.Write("test.trigger", s => s.Execute($"CREATE TRIGGER fail_insert AFTER INSERT ON {table} BEGIN SELECT RAISE(ABORT,'injected'); END"));
        Assert.Throws<SqliteException>(() => Execute(f, Context(f)));
        Assert.Equal((0L, 0L, 0L, 0L), Counts(f));
        Assert.Equal(1, f.Number("SELECT next_seq FROM engine_meta"));
        Assert.Empty(topics.Sent);
    }

    [Fact]
    public void Sequence_uses_durable_cursor_and_returns_last_event_or_zero()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        f.Db.Writer.Write("test.cursor", s => s.Execute("UPDATE engine_meta SET next_seq=42"));
        var c = Context(f);
        var result = f.Db.Writer.Write("test.multiple", s => EngineJournal.Execute(s, c, "{}", () => Transition(s, c, 2)));
        Assert.Equal(43, result.Seq);
        Assert.Equal(new long[] { 42, 43 }, f.Events().Select(e => e.Seq));
        Assert.Equal(43, f.Number("SELECT event_seq FROM engine_outbox"));
        var emptyContext = c with { CommandId = Guid.NewGuid() };
        var empty = f.Db.Writer.Write("test.empty", s => EngineJournal.Execute(s, emptyContext, "{}",
            () => EngineJournal.Commit(s, emptyContext, 1, 0, [], [])));
        Assert.Equal(0, empty.Seq);
        Assert.Equal(44, f.Number("SELECT next_seq FROM engine_meta"));
    }

    [Fact]
    public void Command_budget_is_utf8_and_rejects_before_transition()
    {
        using var f = new EngineFixture();
        var calls = 0;
        var result = f.Db.Writer.Write("test.large", s => EngineJournal.Execute(s, Context(f),
            "\"" + new string('界', 11000) + "\"", () => { calls++; return new(true, false, null, 0, 0, 0); }));
        Assert.False(result.Applied);
        Assert.NotNull(result.Error);
        Assert.Equal(0, calls);
        Assert.Equal(0, f.Count("engine_event"));
    }

    [Fact]
    public void Event_budget_rolls_back_transition()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        var c = Context(f);
        Assert.Throws<InvalidOperationException>(() => f.Db.Writer.Write("test.large-event", s => EngineJournal.Execute(s, c, "{}", () =>
        {
            s.Execute("UPDATE engine_session SET version=1");
            return EngineJournal.Commit(s, c, 1, 0,
                [new("changed", "session", "s", 1, false, new("s", null, null, null, new string('界', 6000), null))], []);
        })));
        Assert.Equal((0L, 0L, 0L, 0L), Counts(f));
    }

    private static (long, long, long, long) Counts(EngineFixture f) =>
        (f.Count("engine_event"), f.Count("engine_outbox"), f.Count("engine_command_receipt"), f.Number("SELECT version FROM engine_session"));

    private sealed class CapturingTopics : ITopics
    {
        public List<string> Sent { get; } = [];
        public Action? OnPublish { get; set; }
        public void Publish(string topic, string? key, string json, bool keyedOnly = false)
        {
            OnPublish?.Invoke();
            Sent.Add(topic);
        }
        public bool HasSubscribers(string topic, string key) => true;
        public event Action<string, string?>? Unsubscribed { add { } remove { } }
    }
}
