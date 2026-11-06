using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Needs;

public sealed class RoutedNeedSchemaTests
{
    [Fact]
    public void Fresh_seven_adds_only_nullable_addressed_session_and_keeps_chat_five()
    {
        using var f = new EngineFixture();
        Assert.Equal(15, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(5, f.Number("PRAGMA user_version"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_needs_me') WHERE name='addressee_session_id' AND type='TEXT' AND [notnull]=0"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM pragma_foreign_key_list('engine_needs_me') WHERE [from]='addressee_session_id' AND [table]='engine_session' AND [to]='id'"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_needs_me') WHERE name LIKE '%answerer%'"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Actual_predecessors_keep_original_version_and_legacy_needs_address_Jesse(int version)
    {
        using var old = new Old(version);
        var before = old.NeedRows();
        var init = old.Db.Writer.Write("test.routed-upgrade", s => EngineSchema.Initialize(s, 9000000));
        Assert.Equal(version, init.PreviousVersion);
        Assert.Equal(15, old.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(before, old.NeedRows());
        Assert.Equal(0, old.Number("SELECT COUNT(*) FROM engine_needs_me WHERE addressee_session_id IS NOT NULL"));
        Assert.Equal(5, old.Number("PRAGMA user_version"));
        Assert.Empty(old.Db.Query("PRAGMA foreign_key_check", r => r.Text(0), default));
        var next = old.Db.Writer.Write("test.reopen-seven", s => EngineSchema.Initialize(s, 9000001));
        Assert.Equal(15, next.PreviousVersion);
        Assert.Equal(init.Machine, next.Machine);
        Assert.Equal(before, old.NeedRows());
    }

    [Fact]
    public void Version_update_failure_rolls_back_column_rows_and_version_together()
    {
        using var old = new Old(6);
        old.Db.Writer.Write("test.reject-seven", s => s.Execute("CREATE TRIGGER reject_seven BEFORE UPDATE OF schema_version ON engine_meta WHEN NEW.schema_version=7 BEGIN SELECT RAISE(ABORT,'reject seven'); END"));
        var before = old.NeedRows();
        Assert.Throws<SqliteException>(() => EngineSchema.Open(old.Db, 9000000));
        Assert.Equal(6, old.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(0, old.Number("SELECT COUNT(*) FROM pragma_table_info('engine_needs_me') WHERE name='addressee_session_id'"));
        Assert.Equal(before, old.NeedRows());
        old.Db.Writer.Write("test.retry-seven", s => s.Execute("DROP TRIGGER reject_seven"));
        EngineSchema.Open(old.Db, 9000000);
        Assert.Equal(15, old.Number("SELECT schema_version FROM engine_meta"));
    }

    [Fact]
    public void Address_requires_real_session_and_future_schema_refuses_without_writes()
    {
        using var f = new EngineFixture(); f.SeedSession();
        f.Db.Writer.Write("test.need", s => s.Execute("INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state) VALUES('n',1,'local',1,1,'s','question','Question','[]','open')"));
        Assert.Throws<SqliteException>(() => f.Db.Writer.Write("test.missing-parent", s => s.Execute("UPDATE engine_needs_me SET addressee_session_id='missing'")));
        f.Db.Writer.Write("test.future", s => s.Execute("UPDATE engine_meta SET schema_version=16"));
        Assert.Throws<InvalidOperationException>(() => EngineSchema.Open(f.Db, 9000000));
        Assert.Equal(16, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(1, f.Count("engine_needs_me"));
    }

    private sealed class Old : IDisposable
    {
        private readonly TempDatabase _file = new();
        internal AppDatabase Db { get; }
        internal Old(int version)
        {
            Db = TestDatabase.Open(_file.Path);
            RowStore.Open(Db);
            var fixtureVersion = Math.Min(version, 5);
            var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", $"old-v{fixtureVersion}.sql"));
            Db.Writer.Write("test.actual-predecessor", s =>
            {
                s.Execute(sql);
                // The actual accepted five-to-six migration, without relabeling a database.
                if (version == 6) MessageExpirySchemaUpgrade.Apply(s, 30);
                return 0;
            });
        }
        internal long Number(string sql) => Db.QueryFirst(sql, r => r.Long(0), default);
        internal string[] NeedRows() => Db.Query("SELECT json_array(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state,provider_request_id,chat_row_id,answer_json) FROM engine_needs_me ORDER BY id", r => r.Text(0), default).ToArray();
        public void Dispose() { Db.Dispose(); _file.Dispose(); }
    }
}
