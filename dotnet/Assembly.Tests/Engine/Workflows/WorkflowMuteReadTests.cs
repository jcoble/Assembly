using Assembly.Engine.Reading;
namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowMuteReadTests
{
    [Fact]
    public void Mute_is_versioned_persisted_and_emits_one_descriptor()
    {
        using var f = new EngineFixture();
        var c = WorkflowControlTests.Command("{\"kind\":\"set_mute\",\"untilMs\":10000}");
        Assert.True(f.Apply(c, "workflow-settings").Applied);
        Assert.Equal("version_conflict", f.Apply(c, "workflow-settings").Error);
        Assert.Single(f.Events(), e => e.Kind == "workflow.settings_changed");
        f.Reopen();
        var reader = new EngineReader(f.Db, f.Machine);
        Assert.Equal(1, reader.WorkflowSettings(default).GetProperty("version").GetInt64());
        Assert.Equal(10000, reader.Entity("workflow_settings", "workflow-settings", default)!.Value.GetProperty("mutedUntil").GetInt64());
        using (var snapshot = reader.Snapshot("runs", null, default))
            Assert.Contains(snapshot.ReadNext().Items, item => item.GetProperty("id").GetString() == "workflow-settings"
                && item.GetProperty("machine").GetString() == f.Machine); // A client refuses a whole cut with a row from no machine.
        Assert.Equal(10000, f.Number("SELECT muted_until FROM engine_workflow_settings"));
        Assert.True(f.Apply(WorkflowControlTests.Command("{\"kind\":\"set_mute\",\"untilMs\":null}"), "workflow-settings", 1).Applied);
        Assert.Null(f.Text("SELECT muted_until FROM engine_workflow_settings"));
    }
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
        Assert.Equal(15, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(10000, f.Number("SELECT muted_until FROM engine_workflow_settings"));
        Assert.Equal(0, f.Number("SELECT version FROM engine_workflow_settings"));
        f.Reopen(); Assert.Equal(10000, f.Number("SELECT muted_until FROM engine_workflow_settings"));
    }

}
