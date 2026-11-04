using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;

namespace Assembly.Tests.Engine.Messages;

public sealed class MessageReadProvenanceTests
{
    [Fact]
    public void Creation_source_and_sequence_are_committed_once_and_survive_receipt_changes_and_reopen()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var sent = f.Apply(new SendMessage(null, "s", "original\nbody", "prompt"), "m");
        Assert.True(sent.Applied);
        var created = f.Number("SELECT message_created_seq FROM engine_message WHERE id='m'");
        Assert.Equal(f.Number("SELECT MIN(seq) FROM engine_event WHERE entity_kind='message' AND entity_id='m' "), created);
        Assert.True(created > 0);
        Assert.Equal(f.Machine, f.Text("SELECT origin_machine FROM engine_message WHERE id='m'"));
        Assert.True(f.Apply(new MessageOutcome("delivered_to_turn", "provider-turn", null), "m", sent.Version,
            actor: new(ActorKind.System, "delivery")).Applied);
        Assert.Equal(created, f.Number("SELECT message_created_seq FROM engine_message WHERE id='m'"));
        f.Reopen();
        Assert.Equal(created, f.Number("SELECT message_created_seq FROM engine_message WHERE id='m'"));
        Assert.Equal(f.Machine, f.Text("SELECT origin_machine FROM engine_message WHERE id='m'"));
        Assert.Equal("original\nbody", f.Text("SELECT text FROM engine_message WHERE id='m'"));
    }

    [Fact]
    public void A_need_answer_released_in_its_creation_write_keeps_its_actual_creation_sequence()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var adapter = new EngineActor(ActorKind.System, "adapter");
        Assert.True(f.Apply(new Assembly.Engine.Sessions.SetSessionState("running", null, null), "s", actor: adapter).Applied);
        f.SeedRun(state: "running"); f.SeedStep(state: "running", sessionId: "s", attempt: 1);
        f.Db.Writer.Write("reported steering fixture", scope => scope.Execute("""
            UPDATE engine_session SET can_steer=1 WHERE id='s';
            INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,late) VALUES('step',1,'running',1000,0);
            """));
        Assert.True(f.Apply(new Assembly.Engine.NeedsMe.OpenNeed("s", "r", "step", "question", "Question", [], null, null, null), "n", actor: adapter).Applied);
        Assert.True(f.Apply(new Assembly.Engine.NeedsMe.ResolveNeed("answered", System.Text.Json.JsonSerializer.SerializeToElement("allow")), "n", version: 1).Applied);
        Assert.True(f.Number("SELECT version FROM engine_message WHERE id='need-answer/n'") > 1);
        var created = f.Number("SELECT message_created_seq FROM engine_message WHERE id='need-answer/n'");
        Assert.True(created > 0);
        Assert.Equal(f.Number("SELECT MIN(seq) FROM engine_event WHERE entity_kind='message' AND entity_id='need-answer/n'"), created);
        Assert.Equal(f.Machine, f.Text("SELECT origin_machine FROM engine_message WHERE id='need-answer/n'"));
    }

    [Fact]
    public void Provenance_and_sequence_roll_back_with_the_message_and_journal()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var before = f.Number("SELECT next_seq FROM engine_meta");
        Assert.Throws<InvalidOperationException>(() => f.Db.Writer.Write<int>("test.rollback", scope =>
        {
            var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "window"), "m", 0, 0, 1000);
            Assert.True(new EngineCommands(f.Machine, f.Limits).Apply(scope, context, new SendMessage(null, "s", "kept together", "prompt")).Applied);
            throw new InvalidOperationException("rollback");
        }));
        Assert.Equal(0, f.Count("engine_message")); Assert.Equal(0, f.Count("engine_event"));
        Assert.Equal(before, f.Number("SELECT next_seq FROM engine_meta"));
    }

    [Fact]
    public void Legacy_migration_backfills_only_a_proven_sender_and_retained_initial_event()
    {
        using var f = new EngineFixture(); MessageReadFixture.Seed(f, "retained", 100, "original");
        var initial = f.Number("SELECT message_created_seq FROM engine_message WHERE id='retained'");
        f.Db.Writer.Write("schema thirteen fixture", scope => scope.Execute("""
            ALTER TABLE engine_message DROP COLUMN origin_machine;
            ALTER TABLE engine_message DROP COLUMN message_created_seq;
            DROP INDEX engine_message_read_order;
            UPDATE engine_meta SET schema_version=13;
            """));
        f.Reopen();
        Assert.Equal(f.Machine, f.Text("SELECT origin_machine FROM engine_message WHERE id='retained'"));
        Assert.Equal(initial, f.Number("SELECT message_created_seq FROM engine_message WHERE id='retained'"));
        Assert.Equal("original", f.Text("SELECT text FROM engine_message WHERE id='retained'"));
    }

    [Fact]
    public void Legacy_migration_retains_unknown_null_sender_and_pruned_creation_as_unknown()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(f.Apply(new SendMessage(null, "s", "owner", "prompt"), "known").Applied);
        f.Db.Writer.Write("legacy", scope =>
        {
            scope.Execute("DELETE FROM engine_outbox; DELETE FROM engine_event; DELETE FROM engine_command_receipt;");
            scope.Execute("UPDATE engine_message SET sender_kind='system' WHERE id='known'");
            scope.Execute("ALTER TABLE engine_message DROP COLUMN origin_machine; ALTER TABLE engine_message DROP COLUMN message_created_seq; DROP INDEX engine_message_read_order;");
            return scope.Execute("UPDATE engine_meta SET schema_version=13");
        });
        f.Reopen();
        Assert.Null(f.Text("SELECT origin_machine FROM engine_message WHERE id='known'"));
        Assert.Null(f.Text("SELECT CAST(message_created_seq AS TEXT) FROM engine_message WHERE id='known'"));
        Assert.Equal("owner", f.Text("SELECT text FROM engine_message WHERE id='known'"));
    }
}
