using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Workflows;

public sealed partial class WorkflowReceiptSubmissionTests
{
    [Theory]
    [InlineData("failed", "idle")]
    [InlineData("failed", "lost")]
    [InlineData("failed", "running")]
    [InlineData("cancelled", "idle")]
    [InlineData("cancelled", "lost")]
    [InlineData("cancelled", "running")]
    public void Latest_released_failed_or_cancelled_worker_ends_without_rewriting_original_outcome(string stepState, string sessionState)
    {
        using var f = new Fixture(); var release = Release(f, "released-failure", stepState == "failed" ? "failed" : "cancelled");
        SeedTerminalOutcome(f, stepState, sessionState);
        var attempt = f.Core.Text("SELECT json_object('state',state,'reason',reason,'ended',ended_ms,'epoch',execution_epoch,'fingerprint',fingerprint) FROM engine_step_attempt WHERE attempt=1");
        if (sessionState == "lost")
        {
            var raw = f.Core.Apply(new SetSessionState("ended", null, "ordinary fact"), "worker",
                version: f.Core.Number("SELECT version FROM engine_session WHERE id='worker'"), epoch: 7, actor: new(ActorKind.System, "provider"));
            Assert.Equal("invalid_transition", raw.Error); Assert.Equal("lost", f.Core.State("engine_session", "worker"));
        }
        var result = ApplyRelease(f, release); Assert.True(result.Applied, result.Error);
        Assert.Equal("ended", f.Core.State("engine_session", "worker"));
        Assert.Equal("native-original", f.Core.Text("SELECT provider_session_id FROM engine_session WHERE id='worker'"));
        Assert.Equal(stepState, f.Core.State("engine_step", "step"));
        Assert.Equal("original reason", f.Core.Text("SELECT outcome_reason FROM engine_step WHERE id='step'"));
        Assert.Equal(attempt, f.Core.Text("SELECT json_object('state',state,'reason',reason,'ended',ended_ms,'epoch',execution_epoch,'fingerprint',fingerprint) FROM engine_step_attempt WHERE attempt=1"));
        Assert.Equal(0, f.Core.Count("engine_workflow_receipt"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind IN ('report_reminder','no_report')"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_message WHERE id='child-ended/worker'"));
        Assert.Equal(result, ApplyRelease(f, release, 9999));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='execution_released'"));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void Earlier_released_execution_cannot_end_worker_after_newer_actual_execution_starts(string stepState)
    {
        using var f = new Fixture(); var release = Release(f, "older", stepState == "failed" ? "failed" : "cancelled");
        SeedTerminalOutcome(f, stepState, "running");
        var batch = new RowBatch();
        batch.Rows.Add(new("newer-running", null, "turn", JsonSerializer.SerializeToElement(new { state = "started" }), 1003));
        RowStore.Open(f.Core.Db).Commit("worker", batch);
        var result = ApplyRelease(f, release); Assert.True(result.Applied, result.Error);
        Assert.Equal("running", f.Core.State("engine_session", "worker"));
        Assert.Equal(stepState, f.Core.State("engine_step", "step"));
        Assert.Equal("original reason", f.Core.Text("SELECT reason FROM engine_step_attempt WHERE attempt=1"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_message WHERE id='child-ended/worker'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='execution_released'"));
    }

    private static void SeedTerminalOutcome(Fixture f, string stepState, string sessionState) => f.Core.Db.Writer.Write("test.terminal-outcome", scope => scope.Execute("""
        UPDATE engine_step SET state=$step,outcome_reason='original reason' WHERE id='step';
        UPDATE engine_step_attempt SET state=$step,reason='original reason',ended_ms=999 WHERE step_id='step' AND attempt=1;
        UPDATE engine_session SET state=$session,provider_session_id='native-original' WHERE id='worker';
        UPDATE engine_run SET state=$run WHERE id='r';
        """, new("$step", stepState), new("$session", sessionState), new("$run", stepState == "cancelled" ? "cancelling" : "running")));
}
