using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowSettlementTests
{
    private static readonly EngineActor System = new(ActorKind.System, "workflow");

    private static void Recompute(EngineFixture f) => f.Db.Writer.Write("test.recompute", s =>
    {
        var c = new CommandContext(Guid.NewGuid(), f.Machine, System, "r", 0, 0, 1000);
        return EngineJournal.Commit(s, c, 0, 0, RunTransitions.Recompute(s, c, "r"), []);
    });

    [Fact]
    public void Workflow_retry_uses_generation_budget_and_keeps_monotonic_attempt_identity()
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedRun(ownerSessionId: "owner", state: "running");
        f.SeedSession("worker", parentId: "owner", runId: "r");
        f.SeedStep("s", state: "failed", sessionId: "worker", attempt: 7, maxAttempts: 2);
        f.Db.Writer.Write("test.redone", s => s.Execute("""
            UPDATE engine_run SET definition_hash='workflow';
            UPDATE engine_step SET generation=3,attempt_in_generation=0;
            """));
        Assert.True(f.Apply(new StartStep("worker"), "s").Applied);
        Assert.Equal(8, f.Number("SELECT attempt FROM engine_step"));
        Assert.Equal(1, f.Number("SELECT attempt_in_generation FROM engine_step"));
        Assert.Equal(3, f.Number("SELECT generation FROM engine_step_attempt"));
        Assert.Equal(1, f.Number("SELECT attempt_in_generation FROM engine_step_attempt"));
        Assert.True(f.Apply(new SetStepState("failed", 8, [], "retry", false), "s", 1, actor: System).Applied);
        Assert.Equal("running", f.State("engine_run", "r"));
        Assert.True(f.Apply(new StartStep("worker"), "s", 2).Applied);
        Assert.True(f.Apply(new SetStepState("failed", 9, [], "exhausted", false), "s", 3, actor: System).Applied);
        Assert.Equal("failed", f.State("engine_run", "r"));
        Assert.Equal(2, f.Number("SELECT attempt_in_generation FROM engine_step"));
        Assert.Equal(new long[] { 8, 9 }, f.Db.Query("SELECT attempt FROM engine_step_attempt ORDER BY attempt", r => r.Long(0), default));
    }

    [Fact]
    public void Legacy_retry_keeps_existing_attempt_budget_even_without_generation_counter()
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedRun(ownerSessionId: "owner", state: "running");
        f.SeedSession("worker", parentId: "owner", runId: "r");
        f.SeedStep("s", state: "failed", sessionId: "worker", attempt: 2, maxAttempts: 2);
        Assert.Equal("invalid_transition", f.Apply(new StartStep("worker"), "s").Error);
        Recompute(f); Assert.Equal("failed", f.State("engine_run", "r"));
    }

    [Theory]
    [InlineData("pending", false)]
    [InlineData("inflight", false)]
    [InlineData("uncertain", false)]
    [InlineData("unavailable", false)]
    [InlineData("done", true)]
    [InlineData("failed", true)]
    [InlineData("dead", true)]
    public void Workflow_success_and_cancellation_wait_for_actual_effect_settlement(string state, bool settled)
    {
        foreach (var cancelling in new[] { false, true })
        {
            using var f = new EngineFixture(); f.SeedSession("owner");
            f.SeedRun(ownerSessionId: "owner", state: cancelling ? "cancelling" : "running");
            f.SeedStep("s", state: "succeeded", attempt: 1);
            f.Db.Writer.Write("test.effect", s =>
            {
                s.Execute("UPDATE engine_run SET definition_hash='workflow'");
                var c = new CommandContext(Guid.NewGuid(), f.Machine, System, "r", 0, 0, 1000);
                EngineJournal.Commit(s, c, 0, 0, [], [new("", "workflow_workspace", null, "r", global::System.Text.Json.JsonSerializer.SerializeToElement(new { }))]);
                return s.Execute("UPDATE engine_outbox SET state=$state", new SqlArg("$state", state));
            });
            Recompute(f);
            Assert.Equal(settled ? cancelling ? "cancelled" : "succeeded" : cancelling ? "cancelling" : "running", f.State("engine_run", "r"));
        }
    }

    [Theory]
    [InlineData("done")]
    [InlineData("failed")]
    [InlineData("dead")]
    public void Command_effect_state_without_execution_receipt_cannot_settle_workflow(string state)
    {
        foreach (var cancelling in new[] { false, true })
        {
            using var f = new EngineFixture(); f.SeedSession("owner");
            f.SeedRun(ownerSessionId: "owner", state: cancelling ? "cancelling" : "running");
            f.SeedStep("s", state: "succeeded", attempt: 1);
            f.Db.Writer.Write("test.command-without-proof", s =>
            {
                s.Execute("UPDATE engine_run SET definition_hash='workflow'; UPDATE engine_step SET kind='command'");
                var c = new CommandContext(Guid.NewGuid(), f.Machine, System, "r", 0, 0, 1000);
                EngineJournal.Commit(s, c, 0, 0, [], [new("", "workflow_command", null, "r",
                    global::System.Text.Json.JsonSerializer.SerializeToElement(new { stepId = "s", generation = 0, attempt = 1 }))]);
                return s.Execute("UPDATE engine_outbox SET state=$state", new SqlArg("$state", state));
            });
            Recompute(f);
            Assert.Equal(cancelling ? "cancelling" : "running", f.State("engine_run", "r"));
        }
    }

    [Fact]
    public void Optional_work_must_settle_and_rule_skip_must_be_authorized_before_workflow_success()
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedRun(ownerSessionId: "owner", state: "running");
        f.SeedStep("required", state: "skipped", attempt: 0, ruleSkippable: true);
        f.SeedStep("optional", required: false);
        f.Db.Writer.Write("test.workflow", s => s.Execute("UPDATE engine_run SET definition_hash='workflow'"));
        Recompute(f); Assert.NotEqual("succeeded", f.State("engine_run", "r"));
        f.Db.Writer.Write("test.optional-settled", s => s.Execute("UPDATE engine_step SET state='failed' WHERE id='optional'"));
        Recompute(f); Assert.NotEqual("succeeded", f.State("engine_run", "r"));
        f.Db.Writer.Write("test.rule", s => s.Execute("UPDATE engine_step SET outcome_reason='condition_false' WHERE id='required'"));
        Recompute(f); Assert.Equal("succeeded", f.State("engine_run", "r"));
    }

    [Fact]
    public void Checked_completion_preserves_summary_and_requires_same_attempt_release_before_success()
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedRun(ownerSessionId: "owner", state: "running");
        f.SeedSession("worker", parentId: "owner", runId: "r", epoch: 4);
        f.SeedStep("s", state: "running", sessionId: "worker", attempt: 8);
        f.Db.Writer.Write("test.checked", s => s.Execute("""
            UPDATE engine_run SET definition_hash='workflow';
            INSERT INTO engine_workflow_workspace(id,version,machine,created_ms,updated_ms,run_id,step_id,allocation_id,path,head,owned,state)
              VALUES('w',1,$machine,1000,1000,'r','s','allocation','/test','head',1,'ready');
            UPDATE engine_step SET generation=3,attempt_in_generation=1,authority_machine=$machine,authority_session_id='worker',authority_epoch=4,workspace_id='w',result='text',receipt_id='receipt';
            INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,session_id,epoch,hash,kind,body_json,state)
              VALUES('receipt',1,$machine,1000,1000,'r','s',8,3,'worker',4,'hash','text','{"report":{"summary":"immutable report summary","proofUrls":["https://example.com/proof"]}}','accepted');
            INSERT INTO engine_step_attempt(step_id,attempt,state,late,generation,attempt_in_generation,session_id,authority_machine,execution_epoch,workspace_id,receipt_id)
              VALUES('s',8,'running',0,3,1,'worker',$machine,4,'w','receipt');
            """, new SqlArg("$machine", f.Machine)));
        var result = f.Db.Writer.Write("test.complete", s => StepTransitions.CompleteCheckedReceipt(s,
            new(Guid.NewGuid(), f.Machine, System, "s", 0, 4, 1000), "receipt"));
        Assert.True(result.Applied, result.Error);
        Assert.Equal("immutable report summary", f.Text("SELECT outcome_reason FROM engine_step"));
        Assert.Equal("running", f.State("engine_run", "r"));
        f.Db.Writer.Write("test.ended", s => s.Execute("UPDATE engine_session SET state='ended' WHERE id='worker'"));
        Recompute(f); Assert.Equal("running", f.State("engine_run", "r"));
        f.Db.Writer.Write("test.old-release", s => s.Execute("""
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
              VALUES('release',1,$machine,1000,1000,'r',1,'s',7,2,'workflow_worker_released',1000,'{"receiptId":"receipt","sessionId":"worker","epoch":4,"workspaceId":"w"}');
            """, new SqlArg("$machine", f.Machine)));
        Recompute(f); Assert.Equal("running", f.State("engine_run", "r"));
        f.Db.Writer.Write("test.actual-release", s => s.Execute("""
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
              SELECT 'release-current',version,machine,created_ms,updated_ms,run_id,2,step_id,8,3,kind,at_ms,safe_body_json
              FROM engine_workflow_history WHERE id='release'
            """));
        Recompute(f); Assert.Equal("succeeded", f.State("engine_run", "r"));
    }
}
