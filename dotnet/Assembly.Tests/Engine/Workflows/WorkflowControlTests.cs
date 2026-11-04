using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Runs;
using Assembly.Engine.Reading;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowControlTests
{
    internal static EngineCommand Command(string json) => JsonSerializer.Deserialize(json, EngineJson.Default.EngineCommand)!;
    internal static EngineFixture Fixture(string state = "running")
    {
        var f = new EngineFixture(); f.SeedSession("owner"); f.SeedRun(ownerSessionId: "owner", state: state);
        f.Db.Writer.Write("test.workflow", s => s.Execute("UPDATE engine_run SET definition_hash='workflow'"));
        return f;
    }
    [Fact]
    public void Pause_resume_are_versioned_and_resume_preserves_open_needs_across_restart()
    {
        using var f = Fixture(); f.SeedStep();
        Assert.True(f.Apply(Command("{\"kind\":\"pause_workflow\"}"), "r").Applied);
        Assert.Equal(1, f.Number("SELECT paused FROM engine_run"));
        Assert.Equal("version_conflict", f.Apply(Command("{\"kind\":\"resume_workflow\"}"), "r").Error);
        f.Reopen();
        Assert.True(f.Apply(Command("{\"kind\":\"resume_workflow\"}"), "r", 1).Applied);
        Assert.Equal(0, f.Number("SELECT paused FROM engine_run"));
        Assert.Equal("invalid_transition", f.Apply(Command("{\"kind\":\"resume_workflow\"}"), "r", 2).Error);
    }
    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("cancelling")]
    public void Terminal_and_cancelling_runs_refuse_pause_and_resume(string state)
    {
        using var f = Fixture(state);
        foreach (var kind in new[] { "pause_workflow", "resume_workflow", "retry_workflow_step" })
            Assert.Equal("invalid_transition", f.Apply(Command($"{{\"kind\":\"{kind}\",\"stepId\":\"step\"}}"), "r").Error);
    }
    [Fact]
    public void Keep_and_undo_change_closure_not_outcome_and_expire_after_five_seconds()
    {
        using var f = Fixture("failed"); f.SeedStep("failed", state: "failed"); f.SeedStep("ok", state: "succeeded");
        var token = Guid.NewGuid();
        Assert.True(f.Apply(Command("{\"kind\":\"keep_succeeded\"}"), "r", commandId: token).Applied);
        Assert.Equal("failed", f.State("engine_run", "r")); Assert.Equal(1, f.Number("SELECT closed FROM engine_step WHERE id='failed'"));
        Assert.Equal(0, f.Number("SELECT closed FROM engine_step WHERE id='ok'"));
        f.Reopen();
        Assert.Equal("invalid_undo", f.Apply(Command($"{{\"kind\":\"undo_keep_succeeded\",\"token\":\"{Guid.NewGuid()}\"}}"), "r", 1).Error);
        Assert.True(f.Apply(Command($"{{\"kind\":\"undo_keep_succeeded\",\"token\":\"{token}\"}}"), "r", 1).Applied);
        Assert.Equal(0, f.Number("SELECT closed FROM engine_step WHERE id='failed'"));
        token = Guid.NewGuid(); Assert.True(f.Apply(Command("{\"kind\":\"keep_succeeded\"}"), "r", 2, commandId: token).Applied);
        f.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("invalid_undo", f.Apply(Command($"{{\"kind\":\"undo_keep_succeeded\",\"token\":\"{token}\"}}"), "r", 3).Error);
    }
    [Fact]
    public void Controls_reject_unrelated_actor_wrong_machine_and_stale_epoch()
    {
        using var f = Fixture(); var command = Command("{\"kind\":\"pause_workflow\"}");
        Assert.Equal("forbidden", f.Apply(command, "r", actor: new(ActorKind.Session, "stranger")).Error);
        Assert.Equal("stale_epoch", f.Apply(command, "r", epoch: 1).Error);
        Assert.True(f.Apply(command, "r", actor: new(ActorKind.Session, "owner")).Applied);
    }
    [Fact]
    public void Closure_fault_rolls_back_steps_run_and_events_together()
    {
        using var f = Fixture("failed"); f.SeedStep(state: "failed");
        f.Db.Writer.Write("test.fail-run", scope => scope.Execute("CREATE TRIGGER fail_run BEFORE UPDATE ON engine_run BEGIN SELECT RAISE(ABORT,'run fault'); END"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => f.Apply(Command("{\"kind\":\"keep_succeeded\"}"), "r"));
        Assert.Equal(0, f.Number("SELECT closed FROM engine_step")); Assert.Equal(0, f.Number("SELECT version FROM engine_run"));
        Assert.Equal(0, f.Count("engine_event")); Assert.Equal(0, f.Count("engine_command_receipt"));
    }

}
