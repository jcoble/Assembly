namespace Assembly.Tests.Engine.Workflows;

public sealed partial class WorkflowReceiptSubmissionTests
{
    [Fact]
    public void Optional_failed_step_allows_success_only_after_actual_execution_release()
    {
        using var f = new Fixture(); var release = Release(f, "optional-failed", "failed");
        f.Core.Db.Writer.Write("test.optional-failure", s => s.Execute("""
            UPDATE engine_run SET definition_hash=(SELECT definition_hash FROM engine_workflow_snapshot) WHERE id='r';
            UPDATE engine_step SET state='failed',required=0,outcome_reason='original failure' WHERE id='step';
            UPDATE engine_step_attempt SET state='failed',reason='original failure' WHERE step_id='step';
            UPDATE engine_session SET state='ended' WHERE id='worker';
            """));
        Assert.Equal("running", f.Core.State("engine_run", "r"));
        var result = ApplyRelease(f, release); Assert.True(result.Applied, result.Error);
        Assert.Equal("succeeded", f.Core.State("engine_run", "r"));
        Assert.Equal("failed", f.Core.State("engine_step", "step"));
        Assert.Equal("original failure", f.Core.Text("SELECT reason FROM engine_step_attempt"));
        Assert.Equal(0, f.Core.Count("engine_workflow_receipt"));
    }

    [Fact]
    public void Actual_execution_release_cannot_replace_accepted_receipt_for_succeeded_step()
    {
        using var f = new Fixture(); var release = Release(f, "unverified-success");
        f.Core.Db.Writer.Write("test.unverified-success", s => s.Execute("""
            UPDATE engine_run SET definition_hash=(SELECT definition_hash FROM engine_workflow_snapshot) WHERE id='r';
            UPDATE engine_step SET state='succeeded' WHERE id='step';
            UPDATE engine_step_attempt SET state='succeeded' WHERE step_id='step';
            UPDATE engine_session SET state='ended' WHERE id='worker';
            """));
        var result = ApplyRelease(f, release); Assert.True(result.Applied, result.Error);
        Assert.Equal("running", f.Core.State("engine_run", "r"));
        Assert.Equal("succeeded", f.Core.State("engine_step", "step"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='execution_released'"));
        Assert.Equal(0, f.Core.Count("engine_workflow_receipt"));
    }
}
