using Assembly.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine;

public sealed class PrSchemaTests
{
    [Fact]
    public void Upgrade_keeps_primary_reference_and_all_existing_pr_bytes_with_foreign_keys_enabled()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        Seed(db);
        db.Writer.Write("test.pr-upgrade", scope => { PrSchemaUpgrade.Apply(scope); return true; });
        Assert.Equal("p|r|org/repo|42|head|merged|claimed|observed", db.QueryFirst(
            "SELECT id||'|'||run_id||'|'||repo||'|'||number||'|'||head||'|'||state||'|'||reported_state||'|'||observed_head FROM engine_pr", r => r.Text(0), default));
        Assert.Equal("p", db.QueryFirst("SELECT pr_id FROM engine_run", r => r.Text(0), default));
        Assert.Empty(db.Query("PRAGMA foreign_key_check", r => r.Text(0), default));
        Assert.Equal(13, db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
        db.Writer.Write("test.runless-pr", scope => scope.Execute("""
            INSERT INTO engine_pr(id,version,machine,created_ms,updated_ms,run_id,owner_session_id,repo,number,state)
            VALUES('owned',1,'machine',1,1,NULL,'owner','org/repo',43,'not_created');
            """));
        Assert.Throws<SqliteException>(() => db.Writer.Write("test.unowned-pr", scope => scope.Execute("""
            INSERT INTO engine_pr(id,version,machine,created_ms,updated_ms,repo,number,state)
            VALUES('unowned',1,'machine',1,1,'org/repo',44,'not_created');
            """)));
    }

    [Fact]
    public void Failure_at_version_advance_rolls_back_columns_rows_and_intent_table_together()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        Seed(db);
        db.Writer.Write("test.reject-pr-upgrade", scope => scope.Execute("""
            CREATE TRIGGER reject_pr_upgrade BEFORE UPDATE OF schema_version ON engine_meta
            WHEN NEW.schema_version=13 BEGIN SELECT RAISE(ABORT,'injected'); END;
            """));
        var before = db.Query("SELECT sql FROM sqlite_master ORDER BY name", r => r.IsNull(0) ? null : r.Text(0), default);
        Assert.Throws<SqliteException>(() => db.Writer.Write("test.pr-upgrade", scope => { PrSchemaUpgrade.Apply(scope); return true; }));
        Assert.Equal(before, db.Query("SELECT sql FROM sqlite_master ORDER BY name", r => r.IsNull(0) ? null : r.Text(0), default));
        Assert.Equal(12, db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
        Assert.Equal("r", db.QueryFirst("SELECT run_id FROM engine_pr", r => r.Text(0), default));
        Assert.Equal("p", db.QueryFirst("SELECT pr_id FROM engine_run", r => r.Text(0), default));
        Assert.Empty(db.Query("PRAGMA foreign_key_check", r => r.Text(0), default));
    }

    private static void Seed(Assembly.Application.Common.Database.IAppDatabase db) => db.Writer.Write("test.pr-v12", scope => scope.Execute("""
        CREATE TABLE engine_meta(singleton INTEGER PRIMARY KEY,schema_version INTEGER NOT NULL);
        INSERT INTO engine_meta VALUES(1,12);
        CREATE TABLE engine_session(id TEXT PRIMARY KEY);
        INSERT INTO engine_session VALUES('owner');
        CREATE TABLE engine_run(id TEXT PRIMARY KEY,pr_id TEXT REFERENCES engine_pr(id));
        INSERT INTO engine_run VALUES('r',NULL);
        CREATE TABLE engine_pr(id TEXT PRIMARY KEY,version INTEGER NOT NULL,machine TEXT NOT NULL,
            created_ms INTEGER NOT NULL,updated_ms INTEGER NOT NULL,task_url TEXT,
            run_id TEXT NOT NULL REFERENCES engine_run(id),repo TEXT NOT NULL,number INTEGER,head TEXT,
            state TEXT NOT NULL,reported_by TEXT,reported_state TEXT,observed_head TEXT,observed_ms INTEGER,url TEXT);
        INSERT INTO engine_pr VALUES('p',7,'machine',1,2,'task','r','org/repo',42,'head','merged','agent','claimed','observed',3,'url');
        UPDATE engine_run SET pr_id='p' WHERE id='r';
        """));
}
