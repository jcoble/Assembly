using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Sessions;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Workflows.Execution;

public sealed partial class WorkflowAdvancementTests
{
    [Fact]
    public void Lost_owner_at_30000_stops_bound_siblings_and_late_actual_release_keeps_failure()
    {
        using var f = OwnerLossFixture();
        f.Core.SeedSession("stale-worker", "running", "owner", "r", epoch: 8);
        f.Core.SeedStep("stale", "r", "running", "stale-worker", 1);
        f.Core.Db.Writer.Write("test.stale-attempt", s => s.Execute("""
            UPDATE engine_step SET kind='agent',authority_session_id='stale-worker',authority_machine=$machine,authority_epoch=7 WHERE id='stale';
            INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,late,generation,execution_epoch,session_id,authority_machine)
            VALUES('stale',1,'running',1000,0,0,7,'stale-worker',$machine);
            """, new SqlArg("$machine", f.Core.Machine)));
        LoseOwner(f);
        f.Core.Time.Advance(TimeSpan.FromMilliseconds(29999));
        Assert.Equal("owner_not_expired", ExpireOwner(f).Error);
        Assert.Equal("running", f.Core.State("engine_run", "r"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='stop'"));
        f.Core.Time.Advance(TimeSpan.FromMilliseconds(1));
        var context = OwnerExpiryContext(f);
        var result = ExpireOwner(f, context);
        Assert.True(result.Applied, result.Error);
        Assert.Equal("failed", f.Core.State("engine_run", "r"));
        Assert.Equal("owner_lost", f.Core.Events().Single(e => e.Kind == "run.changed" && e.Data.State == "failed").Data.Reason);
        Assert.Equal("owner_lost", f.Core.Text("SELECT json_extract(safe_body_json,'$.reason') FROM engine_workflow_history WHERE kind='owner_lost'"));
        Assert.Equal(31000, f.Core.Number("SELECT json_extract(safe_body_json,'$.deadlineMs') FROM engine_workflow_history WHERE kind='owner_lost'"));
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='stop' AND state='pending' AND session_id IN ('review1-worker','review2-worker')"));
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_session WHERE cancel_requested=1"));
        Assert.Equal(1, f.Core.Number("SELECT cancel_requested FROM engine_step_attempt WHERE step_id='tail'"));
        Assert.Equal("running", f.Core.Text("SELECT state FROM engine_step_attempt WHERE step_id='tail'"));
        Assert.Equal(0, f.Core.Number("SELECT cancel_requested FROM engine_session WHERE id='stale-worker'"));
        Assert.Equal(0, f.Core.Number("SELECT cancel_requested FROM engine_session WHERE id='build-worker'"));
        Assert.Equal(0, f.Core.Number("SELECT cancel_requested FROM engine_session WHERE id='owner'"));
        var events = f.Core.Count("engine_event");
        Assert.Equal(result, ExpireOwner(f, context with { NowMs = context.NowMs + 1 }));
        Assert.Equal(events, f.Core.Count("engine_event"));
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='stop'"));

        f.Terminal("review1"); f.Release("review1"); // The real positive release API records the terminal tuple after failure.
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='execution_released' AND step_id='review1'"));
        Assert.True(f.Core.Events().Last().Late);
        Assert.Equal("failed", f.Core.State("engine_run", "r"));
        Assert.Equal("running", f.Core.State("engine_step", "review1"));
        Assert.True(f.Core.Apply(new SetSessionState("ended", null, null), "review1-worker",
            f.Core.Number("SELECT version FROM engine_session WHERE id='review1-worker'"), 7, new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("failed", f.Core.State("engine_run", "r"));
    }

    [Fact]
    public void Owner_resume_before_deadline_prevents_sibling_stop_and_command_cancellation()
    {
        using var f = OwnerLossFixture(); LoseOwner(f);
        f.Core.Time.Advance(TimeSpan.FromMilliseconds(29999));
        Assert.True(f.Core.Apply(new SetSessionState("idle", null, null), "owner",
            f.Core.Number("SELECT version FROM engine_session WHERE id='owner'"), actor: new(ActorKind.System, "adapter")).Applied);
        f.Core.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("owner_not_expired", ExpireOwner(f).Error);
        Assert.Equal("running", f.Core.State("engine_run", "r"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_session WHERE cancel_requested=1"));
        Assert.Equal(0, f.Core.Number("SELECT cancel_requested FROM engine_step_attempt WHERE step_id='tail'"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='stop'"));
    }

    [Fact]
    public void Legacy_owner_expiry_retains_existing_failure_without_workflow_stop_effects()
    {
        using var f = OwnerLossFixture();
        f.Core.Db.Writer.Write("test.legacy-run", s => s.Execute("UPDATE engine_run SET definition_hash=NULL WHERE id='r'"));
        LoseOwner(f); f.Core.Time.Advance(TimeSpan.FromMilliseconds(30000));
        Assert.True(ExpireOwner(f).Applied);
        Assert.Equal("failed", f.Core.State("engine_run", "r"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='stop'"));
        Assert.Equal(0, f.Core.Number("SELECT cancel_requested FROM engine_step_attempt WHERE step_id='tail'"));
    }

    [Fact]
    public void Foreign_workflow_coordinator_cannot_expire_the_run_or_stop_workers()
    {
        using var f = OwnerLossFixture(); LoseOwner(f);
        f.Core.Time.Advance(TimeSpan.FromMilliseconds(30000));
        f.Core.Db.Writer.Write("test.foreign-reference", s => s.Execute("UPDATE engine_run SET machine='foreign-coordinator' WHERE id='r'"));
        var events = f.Core.Count("engine_event");
        Assert.Equal("wrong_machine", ExpireOwner(f).Error);
        Assert.Equal("running", f.Core.State("engine_run", "r"));
        Assert.Equal(events, f.Core.Count("engine_event"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_session WHERE cancel_requested=1"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='stop'"));
    }

    private static Fixture OwnerLossFixture()
    {
        var f = new Fixture(new Topics());
        f.Core.Db.Writer.Write("test.live-siblings-and-command", s => s.Execute("""
            UPDATE engine_run SET owner_loss_limit_ms=30000 WHERE id='r';
            UPDATE engine_session SET state='idle' WHERE id IN ('review1-worker','review2-worker');
            UPDATE engine_step SET state='running',attempt=1,authority_epoch=9,authority_machine=$machine WHERE id='tail';
            INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,late,generation,execution_epoch,authority_machine,gate_state)
            VALUES('tail',1,'running',1000,0,0,9,$machine,'executing');
            """, new SqlArg("$machine", f.Core.Machine)));
        return f;
    }

    private static void LoseOwner(Fixture f) => Assert.True(f.Core.Apply(new SetSessionState("lost", null, null), "owner",
        actor: new(ActorKind.System, "adapter")).Applied);
    private static CommandContext OwnerExpiryContext(Fixture f) => new(Guid.NewGuid(), f.Core.Machine, new(ActorKind.System, "engine.worker"),
        "r", f.Core.Number("SELECT version FROM engine_run WHERE id='r'"), 0, f.Core.Time.GetUtcNow().ToUnixTimeMilliseconds());
    private static CommandResult ExpireOwner(Fixture f, CommandContext? context = null)
    {
        var c = context ?? OwnerExpiryContext(f);
        return f.Core.Db.Writer.Write("test.actual-owner-expiry", s => EngineJournal.Execute(s, c, "expire_lost_owner",
            () => RunTransitions.ExpireLostOwner(s, c, "r")));
    }
}
