using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Runs;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowStepControlTests
{
    [Fact]
    public void Retry_in_open_run_keeps_attempt_history_and_next_attempt_increments_generation_budget()
    {
        using var f = WorkflowControlTests.Fixture(); f.SeedStep(state: "failed", attempt: 1, maxAttempts: 2);
        f.Db.Writer.Write("test.failed-attempt", s => s.Execute("""
            UPDATE engine_step SET kind='agent',attempt_in_generation=1;
            INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,ended_ms,late,attempt_in_generation) VALUES('step',1,'failed',900,1000,0,1);
            """));
        Assert.True(f.Apply(new RetryWorkflowStep("step"), "r").Applied);
        Assert.Equal("pending", f.State("engine_step", "step")); Assert.Equal("failed", f.Text("SELECT state FROM engine_step_attempt"));
        Assert.Equal("invalid_transition", f.Apply(new RetryWorkflowStep("step"), "r", Version(f)).Error);
        f.SeedSession("worker", parentId: "owner", runId: "r");
        Assert.True(f.Apply(new StartStep("worker"), "step", 1).Applied);
        Assert.Equal(2, f.Number("SELECT attempt FROM engine_step")); Assert.Equal(2, f.Number("SELECT attempt_in_generation FROM engine_step"));
        Assert.Equal(2, f.Count("engine_step_attempt"));
    }
    [Fact]
    public void Retry_refuses_active_worker_and_exhausted_budget()
    {
        using var f = WorkflowControlTests.Fixture(); f.SeedSession("worker", state: "running", parentId: "owner", runId: "r");
        f.SeedStep(state: "waiting", sessionId: "worker", attempt: 1, maxAttempts: 2);
        f.Db.Writer.Write("test.lost", s => s.Execute("UPDATE engine_step SET kind='agent',outcome_reason='session_lost',attempt_in_generation=1"));
        Assert.Equal("executor_quiescence_required", f.Apply(new RetryWorkflowStep("step"), "r").Error);
        f.Db.Writer.Write("test.exhausted", s => s.Execute("UPDATE engine_step SET attempt_in_generation=2"));
        Assert.Equal("attempts_exhausted", f.Apply(new RetryWorkflowStep("step"), "r").Error);
    }
    [Fact]
    public void Grant_records_confirmation_not_external_privilege_and_requires_window_and_exact_scope()
    {
        using var f = WorkflowControlTests.Fixture(); f.SeedStep(state: "waiting");
        f.Db.Writer.Write("test.scope", s => s.Execute("UPDATE engine_step SET kind='agent',required_scope='repo:write',outcome_reason='scope_required'"));
        Assert.Equal("forbidden", f.Apply(new GrantWorkflowScope("step", "repo:write"), "r", actor: new(ActorKind.Session, "owner")).Error);
        Assert.Equal("invalid_transition", f.Apply(new GrantWorkflowScope("step", "admin"), "r").Error);
        Assert.True(f.Apply(new GrantWorkflowScope("step", "repo:write"), "r").Applied);
        Assert.Equal("repo:write", f.Text("SELECT json_extract(safe_body_json,'$.scope') FROM engine_workflow_history WHERE kind='scope_confirmed'"));
        Assert.Equal(1, f.Number("SELECT scope_granted FROM engine_step")); Assert.Equal("waiting", f.State("engine_step", "step"));
        Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Equal("invalid_transition", f.Apply(new GrantWorkflowScope("step", "repo:write"), "r", Version(f)).Error);
        Assert.True(f.Apply(new DecideWorkflowStep("step", "continue"), "r", Version(f)).Applied);
        Assert.Equal("pending", f.State("engine_step", "step"));
    }
    [Theory]
    [InlineData("approve", "succeeded")]
    [InlineData("deny", "failed")]
    [InlineData("revise", "pending")]
    public void Gate_owner_answer_resolves_one_need_and_duplicate_is_already_resolved(string decision, string expected)
    {
        using var f = Gate(); var need = f.Text("SELECT id FROM engine_needs_me")!;
        Assert.Equal("forbidden", f.Apply(new DecideWorkflowStep("step", decision, "Fix this"), "r", Version(f), actor: new(ActorKind.Session, "owner")).Error);
        Assert.True(f.Apply(new DecideWorkflowStep("step", decision, "Fix this"), "r", Version(f)).Applied);
        Assert.Equal(expected, f.State("engine_step", "step")); Assert.Equal("answered", f.State("engine_needs_me", need));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='workflow_decision'"));
        Assert.Equal("already_resolved", f.Apply(new DecideWorkflowStep("step", decision, "Fix this"), "r", Version(f)).Error);
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
    }
    [Fact]
    public void Resume_never_clears_gate_and_required_gate_cannot_be_skipped()
    {
        using var f = Gate();
        Assert.True(f.Apply(new PauseWorkflow(), "r", Version(f)).Applied);
        f.Reopen(); Assert.True(f.Apply(new ResumeWorkflow(), "r", Version(f)).Applied);
        Assert.Equal("open", f.Text("SELECT state FROM engine_needs_me"));
        Assert.Equal("required_step", f.Apply(new DecideWorkflowStep("step", "skip"), "r", Version(f)).Error);
        Assert.Equal("reply_required", f.Apply(new DecideWorkflowStep("step", "revise"), "r", Version(f)).Error);
        Assert.Equal("bad_args", f.Apply(new DecideWorkflowStep("step", "invented"), "r", Version(f)).Error);
    }
    [Fact]
    public void Pause_starts_nothing_new_but_current_step_can_finish()
    {
        using var f = WorkflowControlTests.Fixture(); f.SeedStep("current", state: "running", attempt: 1); f.SeedStep("gate");
        f.Db.Writer.Write("test.snapshot", s => s.Execute("""
            UPDATE engine_step SET kind='gate' WHERE id='gate';
            INSERT INTO engine_workflow_snapshot(id,version,machine,created_ms,updated_ms,run_id,schema_version,definition_hash,input_hash,snapshot_json)
            VALUES('r',1,$machine,1000,1000,'r',1,'workflow','inputs','{"snapshot":{"definition":{"steps":{"gate":{"gate":"Approve"}}}}}');
            """, new SqlArg("$machine", f.Machine)));
        Assert.True(f.Apply(new PauseWorkflow(), "r").Applied);
        Assert.Equal(0, f.Db.Writer.Write("test.gates", s => WorkflowGates.OpenReady(s, f.Machine, 1000)));
        Assert.True(f.Apply(new SetStepState("succeeded", 1, ["https://example.com/proof"], null, false), "current", actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("succeeded", f.State("engine_step", "current")); Assert.Equal("pending", f.State("engine_step", "gate"));
        Assert.True(f.Apply(new ResumeWorkflow(), "r", Version(f)).Applied);
        Assert.Equal(1, f.Db.Writer.Write("test.gates", s => WorkflowGates.OpenReady(s, f.Machine, 1000)));
        Assert.Equal("waiting", f.State("engine_step", "gate"));
    }
    [Fact]
    public void Cancel_closes_gate_need_and_attempt_without_requesting_a_provider_stop()
    {
        using var f = Gate();
        Assert.True(f.Apply(new CancelRun(), "r", Version(f)).Applied);
        Assert.Equal("cancelled", f.State("engine_run", "r"));
        Assert.Equal("cancelled", f.Text("SELECT state FROM engine_needs_me"));
        Assert.Equal("cancelled", f.Text("SELECT state FROM engine_step_attempt"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='stop'"));
    }
    [Fact]
    public void Gate_revision_with_default_budget_opens_a_new_generation_and_keeps_old_attempt()
    {
        using var f = Gate();
        f.Db.Writer.Write("test.default-budget", s => s.Execute("UPDATE engine_step SET max_attempts=1"));
        Assert.True(f.Apply(new DecideWorkflowStep("step", "revise", "Please revise"), "r", Version(f)).Applied);
        Assert.Equal(1, f.Db.Writer.Write("test.reopen-revised-gate", s => WorkflowGates.OpenReady(s, f.Machine, 1000)));
        Assert.Equal(1, f.Number("SELECT generation FROM engine_step"));
        Assert.Equal(2, f.Number("SELECT attempt FROM engine_step"));
        Assert.Equal(1, f.Number("SELECT attempt_in_generation FROM engine_step"));
        Assert.Equal(2, f.Count("engine_step_attempt"));
        Assert.Equal("failed", f.Text("SELECT state FROM engine_step_attempt WHERE attempt=1"));
    }
    internal static long Version(EngineFixture f) => f.Number("SELECT version FROM engine_run WHERE id='r'");
    private static EngineFixture Gate()
    {
        var f = WorkflowControlTests.Fixture(); f.SeedStep(maxAttempts: 2);
        f.Db.Writer.Write("test.gate-snapshot", s => s.Execute("""
            UPDATE engine_step SET kind='gate',definition_key='step';
            INSERT INTO engine_workflow_snapshot(id,version,machine,created_ms,updated_ms,run_id,schema_version,definition_hash,input_hash,snapshot_json)
            VALUES('r',1,$machine,1000,1000,'r',1,'workflow','inputs','{"snapshot":{"definition":{"steps":{"step":{"gate":"Approve"}}}}}');
            """, new SqlArg("$machine", f.Machine)));
        Assert.Equal(1, f.Db.Writer.Write("test.open-gate", s => WorkflowGates.OpenReady(s, f.Machine, 1000)));
        return f;
    }
}
