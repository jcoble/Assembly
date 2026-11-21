using Assembly.Engine.Reading;
namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowMuteReadTests
{
    [Fact]
    public void Mute_fault_rolls_back_settings_event_and_receipt_and_refuses_bad_target()
    {
        using var f = new EngineFixture(); var command = WorkflowControlTests.Command("{\"kind\":\"set_mute\",\"untilMs\":10000}");
        Assert.Equal("bad_args", f.Apply(command, "wrong-settings").Error);
        Assert.Equal("stale_epoch", f.Apply(command, "workflow-settings", epoch: 1).Error);
        f.Db.Writer.Write("test.fault", s => s.Execute("CREATE TRIGGER fail_event BEFORE INSERT ON engine_event BEGIN SELECT RAISE(ABORT,'event fault'); END"));
        var receipts = f.Count("engine_command_receipt");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => f.Apply(command, "workflow-settings"));
        Assert.Equal(0, f.Number("SELECT version FROM engine_workflow_settings"));
        Assert.Equal(0, f.Count("engine_event")); Assert.Equal(receipts, f.Count("engine_command_receipt"));
    }
    [Fact]
    public void Version_fourteen_upgrade_preserves_mute_deadline_and_reopens_once()
    {
        using var f = new EngineFixture();
        Assert.True(f.Apply(WorkflowControlTests.Command("{\"kind\":\"set_mute\",\"untilMs\":10000}"), "workflow-settings").Applied);
        f.Db.Writer.Write("test.version14-shape", s => s.Execute("""
            ALTER TABLE engine_workflow_settings DROP COLUMN version;
            ALTER TABLE engine_run DROP COLUMN keep_token;
            ALTER TABLE engine_run DROP COLUMN keep_until_ms;
            ALTER TABLE engine_run DROP COLUMN keep_version;
            UPDATE engine_meta SET schema_version=14;
            """));
        f.Reopen();
        Assert.Equal(EngineFixture.CurrentSchemaVersion, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(10000, f.Number("SELECT muted_until FROM engine_workflow_settings"));
        Assert.Equal(0, f.Number("SELECT version FROM engine_workflow_settings"));
        f.Reopen(); Assert.Equal(10000, f.Number("SELECT muted_until FROM engine_workflow_settings"));
    }

}
