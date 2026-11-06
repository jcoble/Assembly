using Assembly.Application.Common.Database;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Workflows.Persistence;

public sealed class WorkflowSchemaTests
{
    [Fact]
    public void Fresh_engine_uses_the_next_real_current_schema()
    {
        using var f = new EngineFixture();
        Assert.Equal(15, f.Number("SELECT schema_version FROM engine_meta"));
    }

    [Fact]
    public void Workflow_records_and_permanent_history_are_installed()
    {
        using var f = new EngineFixture();
        Assert.Equal(7, f.Number("""
            SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN
            ('engine_workflow_snapshot','engine_workflow_workspace','engine_workflow_receipt',
             'engine_workflow_policy','engine_workflow_history','engine_workflow_start','engine_workflow_settings')
            """));
    }

    [Fact]
    public void Existing_core_rows_gain_explicit_workflow_defaults()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedRun(); f.SeedStep();
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_step') WHERE name='definition_key'"));
        Assert.Null(f.Text("SELECT definition_key FROM engine_step WHERE id='step'"));
        Assert.Equal(0, f.Number("SELECT paused FROM engine_run WHERE id='r'"));
        Assert.Equal("ready", f.Text("SELECT setup_state FROM engine_run WHERE id='r'"));
        Assert.Equal(0, f.Number("SELECT generation FROM engine_step WHERE id='step'"));
    }

    [Fact]
    public void Unknown_newer_schema_is_refused_without_writes()
    {
        using var f = new EngineFixture(); f.SeedSession();
        f.Db.Writer.Write("test.future-schema", s => s.Execute("UPDATE engine_meta SET schema_version=16"));
        var before = f.Number("SELECT next_seq FROM engine_meta");
        Assert.Throws<InvalidOperationException>(() => EngineSchema.Open(f.Db, 2000));
        Assert.Equal(16, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(before, f.Number("SELECT next_seq FROM engine_meta"));
        Assert.Equal(1, f.Count("engine_session"));
    }
    [Fact]
    public void Actual_ten_upgrade_preserves_original_rows_machine_cursors_receipts_and_reservations()
    {
        using var file = new TempDatabase(); using var db = LoadTen(file.Path);
        var columns = OriginalColumns(db);
        var before = OriginalRows(db, columns);
        var machine = db.QueryFirst("SELECT machine_id FROM engine_meta", r => r.Text(0), default);
        Assert.Equal(machine, EngineSchema.Open(db, 2000));
        Assert.Equal(15, db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
        Assert.Equal(before, OriginalRows(db, columns));
        Assert.Empty(db.Query("PRAGMA foreign_key_check", r => r.Text(0), default));
    }

    [Fact]
    public void Version_advance_failure_rolls_back_the_whole_actual_ten_upgrade()
    {
        using var file = new TempDatabase(); using var db = LoadTen(file.Path);
        var columns = OriginalColumns(db); var before = OriginalRows(db, columns);
        db.Writer.Write("test.fail-workflow-upgrade", scope => scope.Execute("""
            CREATE TRIGGER reject_eleven BEFORE UPDATE OF schema_version ON engine_meta
            WHEN NEW.schema_version=11 BEGIN SELECT RAISE(ABORT,'workflow upgrade fault'); END;
            """));
        Assert.Throws<SqliteException>(() => EngineSchema.Open(db, 2000));
        Assert.Equal(10, db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
        Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'engine_workflow_%'", r => r.Long(0), default));
        Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('engine_step') WHERE name='definition_key'", r => r.Long(0), default));
        Assert.Equal(before, OriginalRows(db, columns));
    }

    [Fact]
    public void Receipt_and_permanent_history_survive_reopen()
    {
        using var file = new TempDatabase();
        using (var db = LoadTen(file.Path))
        {
            EngineSchema.Open(db, 2000);
            db.Writer.Write("test.workflow-audit", scope => scope.Execute("""
                INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,epoch,hash,kind,body_json,state)
                SELECT 'receipt',1,machine_id,2000,2000,'old-run','old-step',1,0,0,'verified-hash','review','{"summary":"retained"}','checked' FROM engine_meta;
                INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,generation,kind,at_ms,safe_body_json)
                SELECT 'history',1,machine_id,2000,2000,'old-run',1,0,'receipt_checked',2000,'{"receiptId":"receipt"}' FROM engine_meta;
                """));
        }
        using var reopened = TestDatabase.Open(file.Path); EngineSchema.Open(reopened, 3000);
        Assert.Equal("verified-hash", reopened.QueryFirst("SELECT hash FROM engine_workflow_receipt WHERE id='receipt'", r => r.Text(0), default));
        Assert.Equal("retained", reopened.QueryFirst("SELECT json_extract(body_json,'$.summary') FROM engine_workflow_receipt WHERE id='receipt'", r => r.Text(0), default));
        Assert.Equal(2, reopened.QueryFirst("SELECT COALESCE(MAX(history_seq),0)+1 FROM engine_workflow_history WHERE run_id='old-run'", r => r.Long(0), default));
        Assert.Throws<SqliteException>(() => reopened.Writer.Write("test.duplicate-history", scope => scope.Execute("""
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,generation,kind,at_ms,safe_body_json)
            SELECT 'duplicate',1,machine_id,3000,3000,'old-run',1,0,'receipt_checked',3000,'{}' FROM engine_meta;
            """)));
    }

    private static AppDatabase LoadTen(string path)
    {
        var db = TestDatabase.Open(path);
        db.Writer.Write("test.actual-ten", scope => scope.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Engine", "Workflows", "Fixtures", "schema10", "actual-v10.sql"))));
        return db;
    }

    private static Dictionary<string, string[]> OriginalColumns(AppDatabase db) => db.Query(
        "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'engine_%' ORDER BY name", r => r.Text(0), default)
        .ToDictionary(table => table, table => db.Query($"PRAGMA table_info({table})", r => r.Text(1), default)
            .Where(column => table != "engine_meta" || column != "schema_version").ToArray());

    private static string[] OriginalRows(AppDatabase db, Dictionary<string, string[]> columns) => columns.SelectMany(pair =>
        db.Query($"SELECT json_array({string.Join(',', pair.Value.Select(column => '"' + column + '"'))}) AS body FROM {pair.Key} ORDER BY body",
            r => pair.Key + ":" + r.Text(0), default)).ToArray();

}
