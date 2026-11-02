using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Runs;
using Assembly.Infrastructure.Engine.Workflows;

namespace Assembly.Tests.Engine.Workflows;

public sealed partial class WorkflowControlTests
{
    internal static EngineFixture GateFixture()
    {
        var f = new EngineFixture(); f.SeedSession("owner", epoch: 7); f.SeedRun(ownerSessionId: "owner", state: "running");
        f.SeedStep("gate", maxAttempts: 1); f.SeedStep("after");
        f.Db.Writer.Write("test.gate", s => s.Execute("""
            UPDATE engine_run SET definition_hash='workflow';
            UPDATE engine_step SET kind='gate',definition_key='gate',authority_machine=$machine WHERE id='gate';
            INSERT INTO engine_step_dependency(step_id,dependency_id,optional) VALUES('after','gate',0);
            INSERT INTO engine_workflow_snapshot(id,version,machine,created_ms,updated_ms,run_id,schema_version,definition_hash,input_hash,snapshot_json)
              VALUES('snapshot',1,$machine,1000,1000,'r',1,'workflow','inputs','{"snapshot":{"definition":{"steps":{"gate":{"gate":{"prompt":"Continue?"}}}}}}');
            """, new SqlArg("$machine", f.Machine)));
        return f;
    }

    internal static CommandContext Context(EngineFixture f, string id, long version = 0, EngineActor? actor = null, Guid? command = null) =>
        new(command ?? Guid.NewGuid(), f.Machine, actor ?? new(ActorKind.Window, "owner"), id, version, 7, f.Time.GetUtcNow().ToUnixTimeMilliseconds());
    internal static CommandResult Control(EngineFixture f, WorkflowControl control, string id, long version = 0, EngineActor? actor = null, Guid? command = null) =>
        f.Db.Writer.Write("test.control", s => new global::Assembly.Engine.EngineCommands(f.Machine, f.Limits)
            .ApplyWorkflowControl(s, Context(f, id, version, actor, command), control));
    internal static CommandResult OpenGate(EngineFixture f, Guid? command = null) => f.Db.Writer.Write("test.open-gate", s =>
        new global::Assembly.Engine.EngineCommands(f.Machine, f.Limits).OpenWorkflowGate(s,
            Context(f, "gate", actor: new(ActorKind.System, "engine.worker"), command: command)));

    [Fact]
    public async Task Same_worker_callback_starts_one_pure_gate_without_provider_workspace_or_effect()
    {
        using var f = GateFixture();
        var host = new WorkflowDispatchPreparation(f.Db, new(f.Machine, f.Limits), f.Machine, f.Time, "/unused", "/unused");
        Assert.Empty(await host.PrepareAsync(default));
        Assert.Empty(await host.PrepareAsync(default));
        Assert.Equal("waiting", f.State("engine_step", "gate"));
        Assert.Equal(1, f.Number("SELECT attempt FROM engine_step WHERE id='gate'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_step_attempt"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_needs_me WHERE source_kind='gate' AND state='open'"));
        Assert.Equal("owner", f.Text("SELECT session_id FROM engine_needs_me"));
        using var snapshot = new global::Assembly.Engine.Reading.EngineReader(f.Db, f.Machine).Snapshot("needs_me", null, default);
        var gate = Assert.Single(snapshot.ReadNext().Items);
        Assert.Equal("open", gate.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, gate.GetProperty("addresseeSessionId").ValueKind);
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_session"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_workflow_workspace"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_step WHERE session_id IS NOT NULL OR workspace_id IS NOT NULL"));
        using var binding = JsonDocument.Parse(f.Text("SELECT execution_json FROM engine_needs_me")!);
        Assert.Equal(JsonValueKind.Null, binding.RootElement.GetProperty("commandIndex").ValueKind);
        Assert.Equal(JsonValueKind.Null, binding.RootElement.GetProperty("effectId").ValueKind);
    }

    [Fact]
    public void Owner_approval_resolves_one_need_and_retains_one_immutable_decision_on_replay()
    {
        using var f = GateFixture(); Assert.True(OpenGate(f).Applied);
        var id = Guid.NewGuid();
        var first = Control(f, new DecideWorkflowStep("gate", "approve", "checked"), "gate", 1, command: id);
        Assert.True(first.Applied, first.Error);
        var replay = Control(f, new DecideWorkflowStep("gate", "approve", "checked"), "gate", 1, command: id);
        Assert.Equal(first, replay);
        Assert.Equal("succeeded", f.State("engine_step", "gate"));
        Assert.Equal("answered", f.Text("SELECT state FROM engine_needs_me"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='gate_decided'"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox"));
        Assert.Equal("already_resolved", Control(f, new DecideWorkflowStep("gate", "approve", null), "gate", 2).Error);
        Assert.Equal("pending", f.State("engine_step", "after"));
    }

    [Fact]
    public void Generic_system_fact_cannot_impersonate_owner_gate_approval_or_denial()
    {
        using var f = GateFixture(); Assert.True(OpenGate(f).Applied);
        foreach (var state in new[] { "succeeded", "failed" })
            Assert.Equal("gate_decision_required", f.Apply(new global::Assembly.Engine.Runs.SetStepState(state, 1, ["https://example.com/proof"], "fake", false),
                "gate", 1, 7, new(ActorKind.System, "provider")).Error);
        Assert.Equal("open", f.Text("SELECT state FROM engine_needs_me"));
    }

    [Fact]
    public void Root_can_pause_but_cannot_answer_Jesses_gate_and_resume_preserves_open_need()
    {
        using var f = GateFixture(); Assert.True(OpenGate(f).Applied);
        var root = new EngineActor(ActorKind.Session, "owner");
        Assert.Equal("forbidden", Control(f, new DecideWorkflowStep("gate", "approve", null), "gate", 1, root).Error);
        var version = f.Number("SELECT version FROM engine_run");
        Assert.True(Control(f, new PauseWorkflow(), "r", version, root).Applied);
        Assert.True(Control(f, new ResumeWorkflow(), "r", version + 1, root).Applied);
        Assert.Equal("open", f.Text("SELECT state FROM engine_needs_me"));
        Assert.Equal("waiting", f.State("engine_step", "gate"));
    }

    [Fact]
    public void Gate_answer_does_not_resume_paused_run_and_deny_keeps_actual_failed_outcome()
    {
        foreach (var decision in new[] { "approve", "deny" })
        {
            using var f = GateFixture(); Assert.True(OpenGate(f).Applied);
            Assert.True(Control(f, new PauseWorkflow(), "r", f.Number("SELECT version FROM engine_run")).Applied);
            var result = Control(f, new DecideWorkflowStep("gate", decision, null), "gate", 1);
            Assert.True(result.Applied, result.Error);
            Assert.Equal(1, f.Number("SELECT paused FROM engine_run"));
            Assert.Equal(decision == "approve" ? "succeeded" : "failed", f.State("engine_step", "gate"));
        }
    }

    [Theory]
    [InlineData("UPDATE engine_step SET generation=1 WHERE id='gate'")]
    [InlineData("UPDATE engine_step_attempt SET execution_epoch=8")]
    [InlineData("UPDATE engine_needs_me SET execution_json=json_set(execution_json,'$.effectId','fake')")]
    public void Gate_decision_refuses_stale_or_fake_executor_tuple(string mutation)
    {
        using var f = GateFixture(); Assert.True(OpenGate(f).Applied);
        f.Db.Writer.Write("test.stale-gate", s => s.Execute(mutation));
        Assert.False(Control(f, new DecideWorkflowStep("gate", "approve", null), "gate", 1).Applied);
        Assert.Equal("open", f.Text("SELECT state FROM engine_needs_me"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='gate_decided'"));
    }

    [Fact]
    public async Task Pause_starts_no_new_gate_and_changes_no_current_execution()
    {
        using var f = GateFixture();
        var paused = Control(f, new PauseWorkflow(), "r"); Assert.True(paused.Applied, paused.Error);
        var host = new WorkflowDispatchPreparation(f.Db, new(f.Machine, f.Limits), f.Machine, f.Time, "/unused", "/unused");
        Assert.Empty(await host.PrepareAsync(default));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_step_attempt"));
        Assert.Equal("pending", f.State("engine_step", "gate"));
        Assert.True(Control(f, new ResumeWorkflow(), "r", 1).Applied);
        Assert.Empty(await host.PrepareAsync(default));
        Assert.Equal("waiting", f.State("engine_step", "gate"));
    }
    [Fact]
    public void Cancelling_waiting_gate_settles_exact_need_attempt_and_run_without_stop_effect()
    {
        using var f = GateFixture(); Assert.True(OpenGate(f).Applied);
        Assert.Equal(0, GateDecided(f));
        var cancelled = f.Apply(new global::Assembly.Engine.Runs.CancelRun(), "r", f.Number("SELECT version FROM engine_run"), 7);
        Assert.True(cancelled.Applied, cancelled.Error);
        Assert.Equal("cancelled", f.State("engine_run", "r"));
        Assert.Equal("cancelled", f.State("engine_step", "gate"));
        Assert.Equal("cancelled", f.Text("SELECT state FROM engine_needs_me"));
        Assert.Equal("cancelled", f.Text("SELECT state FROM engine_step_attempt"));
        Assert.Equal(1, GateDecided(f));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox"));
        var archived = f.Text("SELECT safe_body_json FROM engine_workflow_history WHERE kind='gate_decided'");
        Assert.False(Control(f, new DecideWorkflowStep("gate", "approve", null), "gate", 2).Applied);
        Assert.Equal(archived, f.Text("SELECT safe_body_json FROM engine_workflow_history WHERE kind='gate_decided'"));
    }

    [Fact]
    public void Real_gate_decision_is_required_for_pure_attempt_quiescence_and_matches_only_original_epoch()
    {
        using var f = GateFixture(); Assert.True(OpenGate(f).Applied);
        Assert.Equal(0, GateDecided(f));
        Assert.True(Control(f, new DecideWorkflowStep("gate", "approve", null), "gate", 1).Applied);
        Assert.Equal(1, GateDecided(f));
        f.Db.Writer.Write("test.changed-gate-epoch", s => s.Execute("UPDATE engine_step_attempt SET execution_epoch=8"));
        Assert.Equal(0, GateDecided(f));
    }

    [Fact]
    public void Paused_workflow_refuses_direct_core_start_but_current_gate_can_complete()
    {
        using var f = GateFixture(); Assert.True(OpenGate(f).Applied);
        f.SeedSession("worker", parentId: "owner", runId: "r");
        Assert.True(Control(f, new PauseWorkflow(), "r", f.Number("SELECT version FROM engine_run")).Applied);
        Assert.Equal("run_paused", f.Apply(new global::Assembly.Engine.Runs.StartStep("worker"), "after").Error);
        Assert.Equal(0, f.Number("SELECT attempt FROM engine_step WHERE id='after'"));
        Assert.True(Control(f, new DecideWorkflowStep("gate", "approve", null), "gate", 1).Applied);
        Assert.Equal(1, f.Number("SELECT paused FROM engine_run"));
    }

    [Fact]
    public void Current_generation_gate_is_selected_even_when_old_and_new_needs_have_same_clock_time()
    {
        using var f = GateFixture();
        f.Db.Writer.Write("test.old-gate-generation", s => s.Execute("UPDATE engine_step SET generation=9 WHERE id='gate'"));
        Assert.True(OpenGate(f).Applied);
        Assert.True(Control(f, new DecideWorkflowStep("gate", "approve", null), "gate", 1).Applied);
        f.Db.Writer.Write("test.next-gate-generation", s => s.Execute("""
            UPDATE engine_step SET generation=10,state='pending',dispatch_state='pending',attempt_in_generation=0,
              version=version+1 WHERE id='gate'
            """));
        var opened = f.Db.Writer.Write("test.open-next-gate", s => new global::Assembly.Engine.EngineCommands(f.Machine, f.Limits)
            .OpenWorkflowGate(s, Context(f, "gate", 3, new(ActorKind.System, "engine.worker"))));
        Assert.True(opened.Applied, opened.Error);
        var decided = Control(f, new DecideWorkflowStep("gate", "approve", null), "gate", 4);
        Assert.True(decided.Applied, decided.Error);
        Assert.Equal(2, f.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='gate_decided'"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'"));
    }

    private static long GateDecided(EngineFixture f) => f.Number($"SELECT COUNT(*) FROM engine_step_attempt attempt WHERE {WorkflowGateTransitions.DecidedAttemptPredicate}");
}
