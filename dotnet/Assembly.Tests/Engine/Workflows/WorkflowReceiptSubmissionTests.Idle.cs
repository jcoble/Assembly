using System.Text.Json;
using Microsoft.Data.Sqlite;
using Assembly.Engine.Workflows.Reviews;
using System.Collections.Immutable;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Runs;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Sessions;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Tests.Engine.Workflows;

public sealed partial class WorkflowReceiptSubmissionTests
{
    [Fact]
    public void Released_execution_without_report_gets_one_durable_Assembly_reminder_and_replays_after_reopen()
    {
        using var f = new Fixture(); var release = Release(f, "original"); var first = ApplyRelease(f, release);
        Assert.True(first.Applied, first.Error); Assert.Equal("running", f.Core.State("engine_step", "step"));
        Assert.Equal("Assembly", f.Core.Text("SELECT sender_label FROM engine_message WHERE to_session_id='worker'"));
        Assert.Equal("system", f.Core.Text("SELECT sender_kind FROM engine_message WHERE to_session_id='worker'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
        f.Core.Reopen(); Assert.Equal(first, ApplyRelease(f, release));
        PreparedWorkflowRelease regenerated;
        using (var scope = f.Core.Db.OpenSnapshot(default))
            regenerated = EngineCommands.PrepareWorkflowRelease(scope, new("worker", "r", "step", 1, 7), RowStore.TerminalReply(scope, "worker", "original")!)!;
        Assert.NotSame(release, regenerated); Assert.Equal(first, ApplyRelease(f, regenerated, nowMs: 9999));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='execution_released'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='report_reminder'"));
        Assert.Equal(1, f.Core.Number("SELECT reminder_sent FROM engine_step_attempt"));
    }

    [Fact]
    public void Next_distinct_released_turn_after_actual_reminder_delivery_fails_no_report_and_ends_worker()
    {
        using var f = new Fixture(); Assert.True(ApplyRelease(f, Release(f, "original")).Applied);
        DeliverReminder(f, "reminded-turn");
        var second = ApplyRelease(f, Release(f, "reminded-turn")); Assert.True(second.Applied, second.Error);
        Assert.Equal("failed", f.Core.State("engine_step", "step")); Assert.Equal("ended", f.Core.State("engine_session", "worker"));
        Assert.Equal("no_report", f.Core.Text("SELECT reason FROM engine_step_attempt"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_message WHERE to_session_id='worker'"));
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='execution_released'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='no_report'"));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("rejected")]
    public void Existing_report_including_correctable_rejection_does_not_trigger_no_report_reminder(string state)
    {
        using var f = new Fixture(); Assert.True(f.Apply(f.Prepare()).Applied);
        if (state == "rejected") f.Core.Db.Writer.Write("test.reject", s => s.Execute("UPDATE engine_workflow_receipt SET state='rejected',error='fix evidence'"));
        Assert.True(ApplyRelease(f, Release(f, "reported")).Applied);
        Assert.Equal("waiting", f.Core.State("engine_step", "step")); Assert.Equal(0, f.Core.Count("engine_message"));
        Assert.Equal(state, f.Core.Text("SELECT state FROM engine_workflow_receipt"));
    }

    [Theory]
    [InlineData("lost", "failed")]
    [InlineData("ended", "cancelled")]
    public void Lost_or_ended_release_records_actual_terminal_history_without_inferring_recovery_or_failure(string sessionState, string terminalState)
    {
        using var f = new Fixture(); var release = Release(f, "interrupted", terminalState);
        f.Core.Db.Writer.Write("test.recovery", s => s.Execute("UPDATE engine_session SET state=$state WHERE id='worker'", new SqlArg("$state", sessionState)));
        var result = ApplyRelease(f, release); Assert.True(result.Applied); Assert.True(result.Late);
        Assert.Equal("running", f.Core.State("engine_step", "step")); Assert.Equal(sessionState, f.Core.State("engine_session", "worker"));
        Assert.Equal(0, f.Core.Count("engine_message"));
        Assert.Equal(terminalState, f.Core.Text("SELECT json_extract(safe_body_json,'$.terminalState') FROM engine_workflow_history WHERE kind='execution_released'"));
    }

    [Fact]
    public void Reminder_history_is_per_attempt_even_when_current_step_flag_is_stale()
    {
        using var f = new Fixture(); Assert.True(ApplyRelease(f, Release(f, "attempt-one")).Applied);
        f.Core.Db.Writer.Write("test.next-attempt", s => s.Execute("""
            UPDATE engine_step SET attempt=2,generation=1,reminder_sent=1 WHERE id='step';
            INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,late,generation,session_id,execution_epoch,authority_machine)
            VALUES('step',2,'running',1000,0,1,'worker',7,$machine);
            """, new SqlArg("$machine", f.Core.Machine)));
        Assert.True(ApplyRelease(f, Release(f, "attempt-two", attempt: 2)).Applied);
        Assert.Equal("running", f.Core.State("engine_step", "step"));
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='report_reminder'"));
    }

    [Fact]
    public void Uncommitted_terminal_identity_cannot_remind_and_stale_original_epoch_is_history_only()
    {
        using var f = new Fixture(); var release = Release(f, "stale");
        PreparedWorkflowRelease forged;
        using (var scope = f.Core.Db.OpenSnapshot(default)) forged = EngineCommands.PrepareWorkflowRelease(scope, new("worker", "r", "step", 1, 7),
            new("worker", "missing", 999, "finished", 1000, null, "", null, null))!;
        Assert.Equal("workflow_terminal_changed", ApplyRelease(f, forged).Error);
        f.Core.Db.Writer.Write("test.epoch", s => s.Execute("UPDATE engine_session SET epoch=8 WHERE id='worker'; UPDATE engine_step SET authority_epoch=NULL,authority_machine=NULL,authority_session_id=NULL WHERE id='step'"));
        var result = ApplyRelease(f, release); Assert.True(result.Applied); Assert.True(result.Late);
        Assert.Equal(0, f.Core.Count("engine_message"));
        Assert.Equal(7, f.Core.Number("SELECT json_extract(safe_body_json,'$.epoch') FROM engine_workflow_history WHERE kind='execution_released'"));
    }

    [Fact]
    public void Trusted_late_confirmation_appends_history_without_overwriting_original_receipt_or_attempt()
    {
        using var f = new Fixture(); var submitted = f.Prepare(); Assert.True(f.Apply(submitted).Applied);
        var originalBody = f.Core.Text("SELECT body_json FROM engine_workflow_receipt");
        var originalAttempt = f.Core.Text("SELECT json_object('state',state,'reason',reason,'ended',ended_ms,'fingerprint',fingerprint) FROM engine_step_attempt WHERE attempt=1");
        f.Core.Db.Writer.Write("test.new-generation", s => s.Execute("UPDATE engine_step SET attempt=2,generation=1,state='pending',receipt_id=NULL WHERE id='step'"));
        var checkedResult = Confirm(f, submitted); Assert.True(checkedResult.Applied, checkedResult.Error);
        Assert.Equal(originalBody, f.Core.Text("SELECT body_json FROM engine_workflow_receipt"));
        Assert.Equal("pending", f.Core.Text("SELECT state FROM engine_workflow_receipt"));
        Assert.Equal(originalAttempt, f.Core.Text("SELECT json_object('state',state,'reason',reason,'ended',ended_ms,'fingerprint',fingerprint) FROM engine_step_attempt WHERE attempt=1"));
        Assert.Equal("pending", f.Core.State("engine_step", "step"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='receipt_checked'"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='workflow_worker_released'"));
    }

    [Theory]
    [InlineData("failed", "failed")]
    [InlineData("cancelled", "cancelled")]
    public void Current_ended_worker_release_settles_run_without_overwriting_original_outcome(string stepState, string terminal)
    {
        using var f = new Fixture(); var release = Release(f, "stopped", terminal);
        f.Core.Db.Writer.Write("test.stopped", s => s.Execute("""
            UPDATE engine_step SET state=$state,outcome_reason='original outcome' WHERE id='step';
            UPDATE engine_step_attempt SET state=$state,reason='original outcome' WHERE step_id='step';
            UPDATE engine_session SET state='ended' WHERE id='worker';
            UPDATE engine_run SET state=$runState WHERE id='r';
            """, new("$state", stepState), new("$runState", stepState == "cancelled" ? "cancelling" : "running")));
        var result = ApplyRelease(f, release); Assert.True(result.Applied, result.Error);
        Assert.Equal(stepState, f.Core.State("engine_run", "r")); Assert.Equal(stepState, f.Core.State("engine_step", "step"));
        Assert.Equal("original outcome", f.Core.Text("SELECT reason FROM engine_step_attempt"));
        Assert.Equal(0, f.Core.Count("engine_workflow_receipt")); Assert.Equal(0, f.Core.Count("engine_message"));
        using var scope = f.Core.Db.OpenSnapshot(default);
        var exact = new WorkflowExecutionFence("step", 0, 1, 7); var stale = exact with { ExecutionEpoch = 8 };
        Assert.True(WorkflowExecutionReleases.IsReleased(scope, exact)); Assert.False(WorkflowExecutionReleases.IsReleased(scope, stale));
        Assert.Equal(new[] { exact }, WorkflowExecutionReleases.Released(scope, [exact, stale, exact]).ToArray());
    }

    [Fact]
    public void Reminder_sql_failure_rolls_back_release_fact_flags_message_effect_and_journal_together()
    {
        using var f = new Fixture(); var release = Release(f, "atomic"); var journals = f.Core.Count("engine_command_receipt");
        f.Core.Db.Writer.Write("test.reminder-fault", s => s.Execute("CREATE TRIGGER reject_reminder BEFORE INSERT ON engine_message BEGIN SELECT RAISE(ABORT,'reminder fault'); END;"));
        Assert.Throws<SqliteException>(() => ApplyRelease(f, release));
        Assert.Equal(0, f.Core.Count("engine_workflow_history")); Assert.Equal(0, f.Core.Count("engine_message"));
        Assert.Equal(0, f.Core.Number("SELECT reminder_sent FROM engine_step_attempt")); Assert.Equal(journals, f.Core.Count("engine_command_receipt"));
        Assert.Equal(0, f.Core.Count("engine_outbox"));
        f.Core.Db.Writer.Write("test.remove-fault", s => s.Execute("DROP TRIGGER reject_reminder"));
        Assert.True(ApplyRelease(f, release).Applied);
    }

    [Fact]
    public void Closed_release_preparation_refuses_wrong_original_tuple_and_leaves_ordinary_Board_steps_untouched()
    {
        using var f = new Fixture(); _ = Release(f, "board");
        using (var scope = f.Core.Db.OpenSnapshot(default))
            Assert.Throws<ArgumentException>(() => EngineCommands.PrepareWorkflowRelease(scope, new("worker", "r", "step", 1, 99), RowStore.TerminalReply(scope, "worker", "board")!));
        f.Core.Db.Writer.Write("test.board", s => s.Execute("UPDATE engine_step SET result=NULL WHERE id='step'"));
        var journals = f.Core.Count("engine_command_receipt");
        using (var scope = f.Core.Db.OpenSnapshot(default))
            Assert.Null(EngineCommands.PrepareWorkflowRelease(scope, new("worker", "r", "step", 1, 7), RowStore.TerminalReply(scope, "worker", "board")!));
        Assert.Equal(journals, f.Core.Count("engine_command_receipt")); Assert.Equal(0, f.Core.Count("engine_workflow_history"));
        Assert.Equal(0, f.Core.Count("engine_message")); Assert.Equal("running", f.Core.State("engine_step", "step"));
    }

    [Fact]
    public void Earlier_release_does_not_prove_quiescence_for_newer_actual_execution_in_same_attempt()
    {
        using var f = new Fixture(); Assert.True(ApplyRelease(f, Release(f, "first")).Applied);
        var fence = new WorkflowExecutionFence("step", 0, 1, 7);
        using (var scope = f.Core.Db.OpenSnapshot(default)) Assert.True(WorkflowExecutionReleases.IsReleased(scope, fence));
        DeliverReminder(f, "still-running", idle: false);
        var batch = new RowBatch();
        batch.Rows.Add(new("still-running", null, "turn", JsonSerializer.SerializeToElement(new { state = "started" }), 1003));
        RowStore.Open(f.Core.Db).Commit("worker", batch);
        using (var scope = f.Core.Db.OpenSnapshot(default))
        {
            Assert.False(WorkflowExecutionReleases.IsReleased(scope, fence));
            Assert.Empty(WorkflowExecutionReleases.Released(scope, [fence]));
        }
        Assert.Equal("running", f.Core.State("engine_session", "worker"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='execution_released'"));
    }

    private static PreparedWorkflowRelease Release(Fixture f, string chatRunId, string state = "finished", int attempt = 1)
    {
        EnsureReleaseDefinition(f);
        var batch = new RowBatch();
        batch.Rows.Add(new(chatRunId, null, "turn", JsonSerializer.SerializeToElement(new { state = "started" }), 1000));
        batch.Rows.Add(new(chatRunId, null, "turn", JsonSerializer.SerializeToElement(new { state }), 1001));
        RowStore.Open(f.Core.Db).Commit("worker", batch);
        using var scope = f.Core.Db.OpenSnapshot(default);
        return EngineCommands.PrepareWorkflowRelease(scope, new("worker", "r", "step", attempt, 7), RowStore.TerminalReply(scope, "worker", chatRunId)!)!;
    }
    private static void EnsureReleaseDefinition(Fixture f, bool review = false)
    {
        if (f.Core.Count("engine_workflow_snapshot") != 0) return;
        var yaml = review ? "name: Receipts\nsteps:\n  build: {agent: {provider: codex}, prompt: prompt.md, result: build}\n  step: {needs: [build], result: review, review: {of: build, agent: {provider: codex}, prompt: prompt.md}}" : "name: Receipts\nsteps:\n  step:\n    agent: {provider: codex}\n    prompt: prompt.md\n    result: recon";
        var snapshot = WorkflowParser.Parse(new(yaml, ImmutableSortedDictionary<string, string>.Empty.Add("prompt.md", "Recon")));
        var request = new WorkflowStartRequest(Guid.NewGuid(), "r", 0, 0, "owner", "release", "workflow.yml", "project", "Recon", null, JsonSerializer.SerializeToElement(new { }));
        var source = new WorkflowStartBody(new(Guid.NewGuid(), f.Core.Machine, new(ActorKind.System, "fixture"), "r", 0, 0, 1000), request,
            "project", "/tmp/receipt-fixture", snapshot, new(request.Inputs, "inputs"), ImmutableSortedDictionary<string, string>.Empty.Add("step", "step"), "request");
        f.Core.Db.Writer.Write("test.release-definition", s => s.Execute("""
            UPDATE engine_step SET definition_key='step',kind=$kind,result=$result WHERE id='step';
            UPDATE engine_run SET definition_hash=$hash WHERE id='r';
            INSERT INTO engine_workflow_snapshot(id,version,machine,created_ms,updated_ms,run_id,schema_version,definition_hash,input_hash,snapshot_json)
            VALUES('snapshot',1,$machine,1000,1000,'r',1,$hash,'inputs',$body);
            """, new("$machine", f.Core.Machine), new("$kind", review ? "review" : "agent"), new("$result", review ? "review" : "recon"), new("$hash", snapshot.Hash), new("$body", JsonSerializer.Serialize(source, WorkflowPreparationJson.Default.WorkflowStartBody))));
    }
    private static CommandResult ApplyRelease(Fixture f, PreparedWorkflowRelease prepared, long nowMs = 1002) => f.Core.Db.Writer.Write("test.workflow-release", scope =>
        new EngineCommands(f.Core.Machine, f.Core.Limits).ApplyWorkflowRelease(scope, prepared, nowMs));
    private static void DeliverReminder(Fixture f, string chatRunId, bool idle = true)
    {
        var id = f.Core.Text("SELECT id FROM engine_outbox WHERE kind='send'")!;
        var claim = f.Core.Apply(new ClaimEffect(), id, actor: new(ActorKind.System, "worker")); Assert.True(claim.Applied, claim.Error);
        var receipt = new EffectReceipt(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new MessageEffectData("next_turn", chatRunId, null), EngineJson.Default.MessageEffectData), null);
        var completed = f.Core.Apply(new CompleteEffect(receipt), id, version: claim.Version, epoch: 7, actor: new(ActorKind.System, "worker")); Assert.True(completed.Applied, completed.Error);
        if (!idle) return;
        var settled = f.Core.Apply(new SetSessionState("idle", null, null), "worker", version: f.Core.Number("SELECT version FROM engine_session WHERE id='worker'"),
            epoch: 7, actor: new(ActorKind.System, "provider")); Assert.True(settled.Applied, settled.Error);
    }
}
