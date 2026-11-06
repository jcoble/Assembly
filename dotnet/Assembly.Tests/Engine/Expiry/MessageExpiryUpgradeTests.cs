using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Persistence;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Expiry;

public sealed class MessageExpiryUpgradeTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(int.MaxValue)]
    public void Actual_frozen_v5_backfills_original_creation_and_preserves_every_original_engine_value(int minutes)
    {
        using var f = new Old(5);
        var columns = f.Columns(); var original = f.Rows(columns); var machine = f.Text("SELECT machine_id FROM engine_meta");
        var result = f.Db.Writer.Write("test.upgrade", s => EngineSchema.Initialize(s, 99999999, expiryMinutes: minutes));
        Assert.Equal(5, result.PreviousVersion); Assert.Equal(machine, result.Machine);
        Assert.Equal(original, f.Rows(columns));
        Assert.Equal(15, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(3, f.Number("SELECT COUNT(*) FROM engine_message WHERE state IN ('queued','held') AND expires_ms=created_ms+$duration AND typeof(expires_ms)='integer'", new SqlArg("$duration", (long)minutes * 60000)));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_message WHERE id='terminal-message' AND expires_ms IS NULL"));
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id='reserved-message'"));
        var expiry = f.Db.Writer.Write("test.deadline", s => MessageExpiry.NextDeadline(s, result.Machine));
        Assert.Equal(1000L + (long)minutes * 60000, expiry);
        Assert.Empty(f.Db.Query("PRAGMA foreign_key_check", r => r.Text(0), default));
        var before = f.Rows(f.Columns());
        EngineSchema.Open(f.Db, 88888888, expiryMinutes: 1);
        Assert.Equal(before, f.Rows(f.Columns()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Actual_ordered_predecessors_upgrade_to_seven_preserving_legacy_reservations_and_raw_payload(int version)
    {
        using var f = new Old(version);
        var old = f.Text("SELECT receipt_json FROM engine_outbox WHERE id='send/inflight'");
        var claims = f.Number("SELECT attempt FROM engine_outbox WHERE id='send/inflight'");
        var created = f.Number("SELECT created_ms FROM engine_message WHERE id='inflight'");
        var init = f.Db.Writer.Write("test.upgrade", s => EngineSchema.Initialize(s, 900000000, expiryMinutes: 45));
        Assert.Equal(version, init.PreviousVersion); Assert.Equal(15, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(created + 2700000, f.Number("SELECT expires_ms FROM engine_message WHERE id='inflight'"));
        Assert.Equal(old, f.Text("SELECT receipt_json FROM engine_outbox WHERE id='send/inflight'"));
        Assert.Equal(claims, f.Number("SELECT attempt FROM engine_outbox WHERE id='send/inflight'"));
        Assert.Equal(1, f.Number("SELECT attempt FROM engine_message WHERE id='inflight'"));
        Assert.Equal("send/inflight", f.Text("SELECT client_id FROM engine_message WHERE id='inflight'"));
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id='inflight'"));
        Assert.Equal("body inflight", f.Text("SELECT json_extract(payload_json,'$.text') FROM engine_outbox WHERE id='send/inflight'"));
        var c = new CommandContext(Guid.NewGuid(), init.Machine, new(ActorKind.System, "expiry"), "expiry", 0, 0, 900000000);
        var batch = f.Db.Writer.Write("test.expire", s => MessageExpiry.ExpireDue(s, c));
        Assert.DoesNotContain(batch.Events, e => e.EntityId == "inflight" || e.EntityId == "uncertain" || e.EntityId == "unknown-unavailable");
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id='inflight'"));
    }

    [Theory]
    [InlineData("after_alter")]
    [InlineData("middle_backfill")]
    [InlineData("before_version")]
    public void Failed_upgrade_restores_DDL_all_rows_and_version_then_retry_uses_new_snapshot(string point)
    {
        using var f = new Old(5);
        var trigger = point switch
        {
            "after_alter" => "CREATE TRIGGER fail BEFORE UPDATE OF expires_ms ON engine_message BEGIN SELECT RAISE(ABORT,'injected after alter'); END",
            "middle_backfill" => "CREATE TRIGGER fail BEFORE UPDATE OF expires_ms ON engine_message WHEN OLD.id='held-message' BEGIN SELECT RAISE(ABORT,'injected middle'); END",
            _ => "CREATE TRIGGER fail BEFORE UPDATE OF schema_version ON engine_meta WHEN NEW.schema_version=6 BEGIN SELECT RAISE(ABORT,'injected version'); END"
        };
        // SQLite permits UPDATE OF on a not-yet-existing column; the trigger becomes active after ALTER.
        f.Write(trigger);
        var columns = f.Columns(); var rows = f.Rows(columns); var schema = f.Schema();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => EngineSchema.Open(f.Db, 99999999, expiryMinutes: 45));
        Assert.Equal(5, f.Number("SELECT schema_version FROM engine_meta")); Assert.Equal(schema, f.Schema()); Assert.Equal(rows, f.Rows(columns));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_message') WHERE name='expires_ms'"));
        f.Write("DROP TRIGGER fail"); EngineSchema.Open(f.Db, 99999999, expiryMinutes: 60);
        Assert.Equal(3601000, f.Number("SELECT expires_ms FROM engine_message WHERE id='queued-message'"));
    }

    [Theory]
    [InlineData("'bad'")]
    [InlineData("1000.5")]
    [InlineData("-62135596800001")]
    [InlineData("253402300800000")]
    [InlineData("9223372036854775807")]
    public void Invalid_original_creation_fails_atomic_backfill_without_fabricated_dates(string value)
    {
        using var f = new Old(5); f.Write("UPDATE engine_message SET created_ms=" + value + " WHERE id='held-message'");
        var columns = f.Columns(); var rows = f.Rows(columns); var schema = f.Schema();
        var error = Assert.Throws<CallFailed>(() => EngineSchema.Open(f.Db, 9999999, expiryMinutes: 45));
        Assert.Equal("message_expiry_overflow", error.Message); Assert.Equal(rows, f.Rows(columns)); Assert.Equal(schema, f.Schema());
    }

    [Theory]
    [InlineData(-62135596800000L)]
    [InlineData(253402300799999L)]
    public void Supported_date_boundaries_backfill_as_signed_integer(long created)
    {
        using var f = new Old(5); f.Db.Writer.Write("test.boundary", s => s.Execute("UPDATE engine_message SET created_ms=$created WHERE id='held-message'", new SqlArg("$created", created)));
        EngineSchema.Open(f.Db, 0, expiryMinutes: int.MaxValue);
        Assert.Equal(created + (long)int.MaxValue * 60000, f.Number("SELECT expires_ms FROM engine_message WHERE id='held-message'"));
        Assert.Equal("integer", f.Text("SELECT typeof(expires_ms) FROM engine_message WHERE id='held-message'"));
    }

    [Fact]
    public void More_than_one_migration_batch_is_backfilled_without_changing_versions()
    {
        using var f = new Old(5);
        f.Db.Writer.Write("test.backlog", s => s.Execute("""
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<205)
            INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,sender_kind,to_session_id,text,mode,state)
            SELECT 'batch/'||x,7,(SELECT machine_id FROM engine_meta),-1000,1000,'window','queued','body','prompt','queued' FROM n
            """));
        EngineSchema.Open(f.Db, 99999999, expiryMinutes: 45);
        Assert.Equal(205, f.Number("SELECT COUNT(*) FROM engine_message WHERE id LIKE 'batch/%' AND expires_ms=2699000 AND version=7 AND updated_ms=1000"));
    }

    private sealed class Old : IDisposable
    {
        private readonly TempDatabase _file = new();
        internal AppDatabase Db { get; }
        internal Old(int version)
        {
            Db = AppDatabase.Open(_file.Path, TimeProvider.System, new Topics());
            Assembly.Application.Features.Chat.Rows.RowStore.Open(Db);
            Write(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", $"old-v{version}.sql")));
        }
        internal void Write(string sql) => Db.Writer.Write("test.arrange", s => s.Execute(sql));
        internal long Number(string sql, params SqlArg[] args) => Db.QueryFirst(sql, r => r.Long(0), default, args);
        internal string? Text(string sql) => Db.QueryFirst(sql, r => r.IsNull(0) ? null : r.Text(0), default);
        internal Dictionary<string, string[]> Columns() => Db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'engine_%' AND name<>'engine_meta' ORDER BY name", r => r.Text(0), default)
            .ToDictionary(t => t, t => Db.Query($"PRAGMA table_info({t})", r => r.Text(1), default).ToArray());
        internal string Rows(Dictionary<string, string[]> columns) => string.Join("\n", columns.SelectMany(t => Db.Query($"SELECT json_array({string.Join(',', t.Value.Select(c => $"hex(CAST({c} AS BLOB))"))}) FROM {t.Key} ORDER BY rowid", r => t.Key + ":" + r.Text(0), default)));
        internal string Schema() => string.Join("\n", Db.Query("SELECT type||':'||name||':'||COALESCE(sql,'') FROM sqlite_master ORDER BY name", r => r.Text(0), default));
        public void Dispose() { Db.Dispose(); _file.Dispose(); }
    }
}
