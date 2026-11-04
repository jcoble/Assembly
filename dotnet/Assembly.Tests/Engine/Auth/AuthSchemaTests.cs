using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthSchemaTests
{
    [Fact]
    public void Fresh_auth_schema_has_one_order_key_nullable_safe_facts_and_required_attempt_deadline()
    {
        using var f = new EngineFixture();
        Assert.Equal(15, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(5, f.Number("PRAGMA user_version"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_outbox') WHERE name='order_key' AND type='TEXT' AND [notnull]=1"));
        Assert.Equal(2, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_provider') WHERE name IN ('facts_json','checked_ms') AND [notnull]=0"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_sign_in') WHERE name='deadline_ms' AND [notnull]=1"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM pragma_foreign_key_list('engine_needs_me') WHERE [from]='addressee_session_id' AND [table]='engine_session'"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_sign_in') WHERE name IN ('code','url','challenge','argv','env')"));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public void Actual_six_and_seven_upgrade_without_rewriting_original_rows_or_receipt_bytes(int version)
    {
        using var old = new Predecessor(version);
        var before = old.RetainedRows();
        var identity = old.Meta();
        var result = old.Db.Writer.Write("test.auth.initialize", s => EngineSchema.Initialize(s, 9_000_000));
        Assert.Equal(version, result.PreviousVersion);
        Assert.Equal(15, old.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(identity, old.Meta());
        Assert.Equal(before, old.RetainedRows());
        Assert.Equal(new[] { "effect/legacy-null", "session/%_literal/" }, old.Db.Query("SELECT order_key FROM engine_outbox ORDER BY id", r => r.Text(0), default));
        Assert.Equal(0, old.Number("SELECT COUNT(*) FROM engine_needs_me WHERE addressee_session_id IS NOT NULL"));
        Assert.Empty(old.Db.Query("PRAGMA foreign_key_check", r => r.Text(0), default));
        var reopen = old.Db.Writer.Write("test.auth.reopen", s => EngineSchema.Initialize(s, 9_000_001));
        Assert.Equal(15, reopen.PreviousVersion);
        Assert.Equal(result.Machine, reopen.Machine);
        Assert.Equal(before, old.RetainedRows());
    }

    [Theory]
    [InlineData("engine_provider")]
    [InlineData("engine_sign_in")]
    [InlineData("engine_outbox_order")]
    [InlineData("meta")]
    public void Any_auth_migration_failure_rolls_back_supplied_writer_and_retries(string boundary)
    {
        using var old = new Predecessor(7);
        old.Db.Writer.Write("test.auth.fault", s => s.Execute(boundary switch
        {
            "meta" => "CREATE TRIGGER reject_auth BEFORE UPDATE OF schema_version ON engine_meta WHEN NEW.schema_version=8 BEGIN SELECT RAISE(ABORT,'auth fault'); END",
            "engine_outbox_order" => "CREATE INDEX engine_outbox_order ON engine_outbox(id)",
            _ => $"CREATE TABLE {boundary}(blocker TEXT)"
        }));
        var before = old.RetainedRows(); var schema = old.Schema(); var meta = old.Meta();
        Assert.Throws<SqliteException>(() => old.Db.Writer.Write("test.auth.failed", s =>
        {
            s.Execute("UPDATE engine_meta SET next_seq=9999");
            return EngineSchema.Initialize(s, 9_000_000);
        }));
        Assert.Equal(7, old.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(0, old.Number("SELECT COUNT(*) FROM pragma_table_info('engine_outbox') WHERE name='order_key'"));
        Assert.Equal(before, old.RetainedRows()); Assert.Equal(schema, old.Schema()); Assert.Equal(meta, old.Meta());
        old.Db.Writer.Write("test.auth.unblock", s => s.Execute(boundary switch
        {
            "meta" => "DROP TRIGGER reject_auth",
            "engine_outbox_order" => "DROP INDEX engine_outbox_order",
            _ => $"DROP TABLE {boundary}"
        }));
        EngineSchema.Open(old.Db, 9_000_000);
        Assert.Equal(15, old.Number("SELECT schema_version FROM engine_meta"));
    }

    [Fact]
    public void Failed_projection_rolls_back_before_auth_upgrade_and_newer_version_refuses_unchanged()
    {
        using var old = new Predecessor(7); var schema = old.Schema(); var retained = old.RetainedRows();
        Assert.Throws<InvalidOperationException>(() => EngineSchema.Open(old.Db, 9_000_000, s =>
        {
            s.Execute("CREATE TABLE projection_fault(value TEXT)"); throw new InvalidOperationException("projection fault");
        }));
        Assert.Equal(schema, old.Schema()); Assert.Equal(retained, old.RetainedRows());
        old.Db.Writer.Write("test.auth.future", s => s.Execute("UPDATE engine_meta SET schema_version=16"));
        var meta = old.Meta();
        Assert.Throws<InvalidOperationException>(() => EngineSchema.Open(old.Db, 9_000_000));
        Assert.Equal(16, old.Number("SELECT schema_version FROM engine_meta")); Assert.Equal(meta, old.Meta());
        Assert.Equal(schema, old.Schema()); Assert.Equal(retained, old.RetainedRows());
    }

    private sealed class Predecessor : IDisposable
    {
        private readonly TempDatabase _file = new();
        internal AppDatabase Db { get; }
        internal Predecessor(int version)
        {
            Db = TestDatabase.Open(_file.Path); RowStore.Open(Db);
            var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", "old-v5.sql"));
            Db.Writer.Write("test.auth.actual-predecessor", s =>
            {
                s.Execute(sql);
                // Real accepted v5 -> v6 -> needs7 producers, frozen in the dependency checkpoint.
                MessageExpirySchemaUpgrade.Apply(s, 30);
                if (version == 7) RoutedNeedSchemaUpgrade.Apply(s);
                s.Execute("""
                    INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,folder,title,state,activity_state,holder,epoch,cancel_requested)
                    SELECT 'session/%_literal/',0,machine_id,1,1,'codex','/test','Literal','idle','idle',machine_id,0,0 FROM engine_meta;
                    DELETE FROM engine_outbox;
                    INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt,receipt_json)
                    SELECT 'legacy-null',7,machine_id,10,20,2,NULL,'opaque','{ "exact" : "payload" }','uncertain',3,'{ "disposition":4,"data":null,"error":"exact" }' FROM engine_meta;
                    INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt,receipt_json)
                    SELECT 'legacy-session',8,machine_id,11,21,2,'session/%_literal/','send','{ "old" : null }','done',4,'{ "exact" : "receipt" }' FROM engine_meta;
                    """);
                return 0;
            });
        }
        internal long Number(string sql) => Db.QueryFirst(sql, r => r.Long(0), default);
        internal string Meta() => Db.QueryFirst("SELECT json_array(machine_id,next_seq,retained_after,snapshot_seq) FROM engine_meta", r => r.Text(0), default)!;
        internal string?[] Schema() => Db.Query("SELECT sql FROM sqlite_master ORDER BY name", r => r.IsNull(0) ? null : r.Text(0), default).ToArray();
        internal string[] RetainedRows()
        {
            var tables = Db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'engine_%' AND name NOT IN ('engine_meta','engine_provider','engine_sign_in') AND name NOT LIKE 'engine_workflow_%' ORDER BY name", r => r.Text(0), default);
            return tables.SelectMany(table =>
            {
                var columns = Db.Query($"PRAGMA table_info({table})", r => r.Text(1), default)
                    .Where(column => column != "order_key" && column != "executor_json" && column != "addressee_session_id" &&
                        (table != "engine_message" || column is not ("origin_machine" or "message_created_seq")) &&
                        (table != "engine_session" || column is not ("project_id" or "branch" or "checkout_state" or "checkout_operation_id" or "checkout_intent_json")))
                    .OrderBy(column => column, StringComparer.Ordinal);
                return Db.Query($"SELECT json_array({string.Join(',', columns)}) FROM {table} ORDER BY 1", r => table + ":" + r.Text(0), default);
            }).ToArray();
        }
        public void Dispose() { Db.Dispose(); _file.Dispose(); }
    }
}
