using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Engine.Workflows.Scheduling;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Workflows;

public sealed partial class WorkflowReceiptSubmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_lost_workflow_worker_pushes_waiting_and_retains_real_schedule_reservation(bool review)
    {
        using var f = new Fixture(); EnsureReleaseDefinition(f, review);
        f.Core.SeedStep("next");
        f.Core.Db.Writer.Write("test.next-ready", s => s.Execute("""
            UPDATE engine_run SET max_sessions=1,setup_state='ready' WHERE id='r';
            INSERT INTO engine_workflow_workspace(id,version,machine,created_ms,updated_ms,run_id,step_id,allocation_id,path,base_head,head,owned,state)
            SELECT 'next-ws',1,machine,1000,1000,run_id,'next','next-allocation',path,base_head,head,owned,state FROM engine_workflow_workspace WHERE id='ws';
            UPDATE engine_step SET kind='agent',workspace_id='next-ws' WHERE id='next';
            """));
        Assert.Empty(ReadyAfterLoss(f));
        var effects = f.Core.Count("engine_outbox"); var result = ObserveLost(f); Assert.True(result.Applied, result.Error);
        Assert.Equal("lost", f.Core.State("engine_session", "worker"));
        Assert.Equal("waiting", f.Core.State("engine_step", "step"));
        Assert.Equal("waiting", f.Core.Text("SELECT state FROM engine_step_attempt WHERE step_id='step'"));
        Assert.Equal("session_lost", f.Core.Text("SELECT reason FROM engine_step_attempt WHERE step_id='step'"));
        Assert.Equal("session_lost", f.Core.Text("SELECT outcome_reason FROM engine_step WHERE id='step'"));
        Assert.Contains(f.Core.Events(), e => e.Kind == "step.changed" && e.EntityId == "step" && e.Data.State == "waiting");
        Assert.Empty(ReadyAfterLoss(f)); Assert.Equal(effects, f.Core.Count("engine_outbox"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='session_lost'"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='execution_released'"));
        f.Core.Db.Writer.Write("test.prove-reservation", s => s.Execute("UPDATE engine_step SET state='failed' WHERE id='step'; UPDATE engine_session SET state='ended' WHERE id='worker'"));
        Assert.Equal(new[] { "next" }, ReadyAfterLoss(f));
    }

    [Theory]
    [InlineData("stale_epoch")]
    [InlineData("terminal_run")]
    [InlineData("terminal_attempt")]
    [InlineData("wrong_authority")]
    [InlineData("legacy")]
    public void Lost_observation_does_not_rewrite_stale_terminal_unbound_or_legacy_work(string condition)
    {
        using var f = new Fixture(); EnsureReleaseDefinition(f);
        f.Core.Db.Writer.Write("test.loss-guard", s => s.Execute(condition switch
        {
            "terminal_run" => "UPDATE engine_run SET state='failed' WHERE id='r'",
            "terminal_attempt" => "UPDATE engine_step_attempt SET state='failed',reason='original' WHERE step_id='step'",
            "wrong_authority" => "UPDATE engine_step SET authority_epoch=6 WHERE id='step'",
            "legacy" => "UPDATE engine_run SET definition_hash=NULL WHERE id='r'",
            _ => "UPDATE engine_session SET epoch=8 WHERE id='worker'"
        }));
        var step = f.Core.Text("SELECT json_object('state',state,'reason',outcome_reason,'version',version) FROM engine_step WHERE id='step'");
        var attempt = f.Core.Text("SELECT json_object('state',state,'reason',reason,'ended',ended_ms) FROM engine_step_attempt WHERE step_id='step'");
        var result = ObserveLost(f); Assert.Equal(condition != "stale_epoch", result.Applied);
        if (condition == "stale_epoch") Assert.Equal("stale_epoch", result.Error);
        Assert.Equal(step, f.Core.Text("SELECT json_object('state',state,'reason',outcome_reason,'version',version) FROM engine_step WHERE id='step'"));
        Assert.Equal(attempt, f.Core.Text("SELECT json_object('state',state,'reason',reason,'ended',ended_ms) FROM engine_step_attempt WHERE step_id='step'"));
        Assert.Equal(0, f.Core.Count("engine_workflow_history"));
        if (condition == "terminal_run") Assert.Equal("failed", f.Core.State("engine_run", "r"));
    }

    [Fact]
    public void Lost_workflow_history_failure_rolls_back_session_step_attempt_and_events()
    {
        using var f = new Fixture(); EnsureReleaseDefinition(f); var events = f.Core.Count("engine_event");
        f.Core.Db.Writer.Write("test.loss-fault", s => s.Execute("CREATE TRIGGER reject_loss BEFORE INSERT ON engine_workflow_history BEGIN SELECT RAISE(ABORT,'loss fault'); END"));
        Assert.Throws<SqliteException>(() => ObserveLost(f));
        Assert.Equal("idle", f.Core.State("engine_session", "worker")); Assert.Equal("running", f.Core.State("engine_step", "step"));
        Assert.Equal("running", f.Core.Text("SELECT state FROM engine_step_attempt WHERE step_id='step'")); Assert.Equal(events, f.Core.Count("engine_event"));
    }

    private static CommandResult ObserveLost(Fixture f) => f.Core.Apply(new SetSessionState("lost", null, "provider disconnected"), "worker",
        version: f.Core.Number("SELECT version FROM engine_session WHERE id='worker'"), epoch: 7, actor: new(ActorKind.System, "provider"));
    private static IReadOnlyList<string> ReadyAfterLoss(Fixture f)
    {
        using var scope = f.Core.Db.OpenSnapshot(default);
        return WorkflowSchedule.Ready(scope, f.Core.Machine, 1000, 10);
    }
}
