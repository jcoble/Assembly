using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Engine;
using Assembly.Application.Common.Agents;
using Assembly.Domain.Sessions;
using Assembly.Engine;
using Assembly.Engine.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine;

public sealed class SessionUpgradeTests
{
    [Fact]
    public void Fresh_v5_allows_unknown_model_and_records_account_context()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var machine = EngineSchema.Open(db, 1000);
        Assert.Equal(10, db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
        db.Writer.Write("test.null-model", scope => scope.Execute("""
            INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,state,
                activity_state,holder,epoch,cancel_requested,account_context)
            VALUES('unknown',1,$machine,1000,1000,'retired-custom',NULL,'/test','Unknown','idle','idle','local',0,0,'')
            """, new SqlArg("$machine", machine)));
        Assert.Equal(0, db.QueryFirst("SELECT \"notnull\" FROM pragma_table_info('engine_session') WHERE name='model'", r => r.Long(0), default));
        Assert.Equal(0L, db.QueryFirst("SELECT managed_checkout FROM engine_session WHERE id='unknown'", r => r.Long(0), default));
        Assert.Throws<SqliteException>(() => db.Writer.Write("test.invalid-mode", scope => scope.Execute("UPDATE engine_session SET managed_checkout=2 WHERE id='unknown'")));
        Assert.Empty(db.Query("PRAGMA foreign_key_check", r => r.Text(0), default));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Frozen_versions_upgrade_with_history_indices_and_references_unchanged(int version)
    {
        using var f = new OldDb(version);
        var columns = f.Columns();
        if (version < 3)
            foreach (var converted in new[] { "engine_message", "engine_outbox", "engine_event", "engine_meta" }) columns.Remove(converted);
        var before = f.History(columns); var indexes = f.Indices(); var keys = f.ForeignKeys();
        var machine = f.Text("SELECT machine_id FROM engine_meta");
        Assert.Equal(machine, EngineSchema.Open(f.Db, 10000));
        Assert.Equal(10, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(before, f.History(columns)); Assert.Equal(indexes, f.Indices()); Assert.Equal(keys, f.ForeignKeys());
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM pragma_foreign_key_list('engine_needs_me') WHERE [from]='addressee_session_id' AND [table]='engine_session' AND [to]='id'"));
        Assert.Equal(0, f.Number("SELECT \"notnull\" FROM pragma_table_info('engine_session') WHERE name='model'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_session') WHERE name='account_context'"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_session WHERE account_context<>''"));
        Assert.Empty(f.Db.Query("PRAGMA foreign_key_check", r => r.Text(0), default));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_message WHERE framed<>1 OR attachment_ids_json<>'[]' OR held_epoch IS NOT NULL OR target_epoch IS NOT NULL OR owner_content_sha256 IS NOT NULL"));
        Assert.Equal(machine, EngineSchema.Open(f.Db, 11000)); Assert.Equal(before, f.History(columns));
    }

    [Theory]
    [InlineData("UPDATE engine_session SET model_v4=model")]
    [InlineData("ALTER TABLE session_config ADD COLUMN pending_model")]
    [InlineData("UPDATE engine_meta SET schema_version=4")]
    public void Supplied_scope_upgrade_failure_rolls_back_schema_projection_and_history(string failAfter)
    {
        using var f = new OldDb(3); var sessions = SessionStore.Open(f.Db, f.Time, "/checkouts");
        var before = f.Snapshot(); var published = 0;
        using var subscription = f.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => Interlocked.Increment(ref published));
        Assert.Throws<IOException>(() => f.Db.Writer.Write("test.atomic-init", scope => Initialize(new FaultScope(scope, failAfter),
            s => ProjectionSchema(s))));
        Assert.Equal(before, f.Snapshot()); Assert.Equal(3, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(0, published);
    }

    [Fact]
    public void Actual_v4_composer_upgrade_failure_rolls_back_all_added_columns_and_history()
    {
        using var f = new OldDb(4);
        var before = f.Snapshot();
        Assert.Throws<IOException>(() => f.Db.Writer.Write("test.failed-composer", scope =>
            Initialize(new FaultScope(scope, "UPDATE engine_meta SET schema_version=5"))));
        Assert.Equal(before, f.Snapshot()); Assert.Equal(4, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_message') WHERE name='framed'"));
        Assert.Equal(f.Text("SELECT machine_id FROM engine_meta"), EngineSchema.Open(f.Db, 10000));
        Assert.Equal(10, f.Number("SELECT schema_version FROM engine_meta"));
    }

    [Theory]
    [InlineData("CREATE INDEX unexpected_model ON engine_session(model)")]
    [InlineData("CREATE TRIGGER unexpected_model AFTER UPDATE ON engine_session BEGIN SELECT NEW.model; END")]
    public void Model_column_dependency_refuses_without_rebuilding_or_disabling_foreign_keys(string dependency)
    {
        using var f = new OldDb(3);
        f.Db.Writer.Write("test.model-index", scope => scope.Execute(dependency));
        var before = f.Snapshot();
        var error = Record.Exception(() => EngineSchema.Open(f.Db, 10000));
        Assert.NotNull(error); Assert.True(error is InvalidOperationException or SqliteException);
        Assert.Equal(before, f.Snapshot()); Assert.Equal(1, f.Number("PRAGMA foreign_keys")); Assert.Equal(0, f.Number("PRAGMA writable_schema"));
    }

    [Fact]
    public void Provenance_backfill_uses_the_original_upgrade_fact_once_and_reopen_keeps_explicit_direct_zero()
    {
        using var f = new OldDb(3); var store = SessionStore.Open(f.Db, f.Time, "/checkouts");
        store.Add("pending", "codex", "actual-repo", "");
        store.Add("transient", "codex", "foreign-repo", "peer");
        store.Add("inflight", "codex", "native-repo", "", parentSessionId: "pending");
        var fresh = store.Create("retired", "new-repo");
        f.Db.Writer.Write("test.actual-managed-path", scope => scope.Execute("""
            UPDATE engine_session SET folder='/checkouts/pending' WHERE id='pending';
            UPDATE engine_session SET folder='/checkouts/transient' WHERE id='transient';
            UPDATE engine_session SET folder='/checkouts/pending' WHERE id='inflight';
            """));
        var cutover = new SessionCutover(f.Db, store, new NoLauncher(), "/checkouts", f.Time);
        var first = f.Db.Writer.Write("test.initialize", scope =>
        {
            var schema = EngineSchema.Initialize(scope, 10000, cutover.EnsureSchema);
            cutover.Initialize(scope, new(schema.Machine, new(() => 1, () => 5)), schema.Machine, 10000, schema.PreviousVersion is 1 or 2 or 3);
            return schema;
        });
        Assert.Equal(3, first.PreviousVersion);
        Assert.Equal(1, f.Number("SELECT managed_checkout FROM engine_session WHERE id='pending'"));
        Assert.Equal(1, f.Number($"SELECT managed_checkout FROM engine_session WHERE id='{fresh.SessionId}'"));
        Assert.Equal(0, f.Number("SELECT managed_checkout FROM engine_session WHERE id='inflight'"));
        Assert.Equal(0, f.Number("SELECT managed_checkout FROM engine_session WHERE id='transient'"));
        Assert.Equal(0, f.Number("SELECT managed_checkout FROM engine_session WHERE id='uncertain'"));
        f.Db.Writer.Write("test.persisted-direct", scope => scope.Execute("UPDATE engine_session SET managed_checkout=0 WHERE id='pending'"));
        var next = f.Db.Writer.Write("test.reopen", scope =>
        {
            var schema = EngineSchema.Initialize(scope, 11000, cutover.EnsureSchema);
            cutover.Initialize(scope, new(schema.Machine, new(() => 1, () => 5)), schema.Machine, 11000, schema.PreviousVersion is 1 or 2 or 3);
            return schema;
        });
        Assert.Equal(10, next.PreviousVersion); Assert.Equal(first.Machine, next.Machine);
        Assert.Equal(0, f.Number("SELECT managed_checkout FROM engine_session WHERE id='pending'"));
    }

    [Fact]
    public void Provenance_backfill_failure_rolls_back_schema_mode_and_history_together()
    {
        using var f = new OldDb(3); var store = SessionStore.Open(f.Db, f.Time, "/checkouts");
        store.Add("pending", "codex", "actual-repo", "");
        f.Db.Writer.Write("test.managed-path", scope => scope.Execute("UPDATE engine_session SET folder='/checkouts/pending' WHERE id='pending'"));
        var before = f.Snapshot(); var cutover = new SessionCutover(f.Db, store, new NoLauncher(), "/checkouts", f.Time);
        Assert.Throws<IOException>(() => f.Db.Writer.Write("test.failed-backfill", scope =>
        {
            var failing = new FaultScope(scope, "UPDATE engine_session SET managed_checkout=1");
            var schema = EngineSchema.Initialize(failing, 10000, cutover.EnsureSchema);
            cutover.Initialize(failing, new(schema.Machine, new(() => 1, () => 5)), schema.Machine, 10000, schema.PreviousVersion is 1 or 2 or 3);
            return schema;
        }));
        Assert.Equal(before, f.Snapshot()); Assert.Equal(3, f.Number("SELECT schema_version FROM engine_meta"));
    }

    private sealed class NoLauncher : IAgentLauncher
    {
        public IReadOnlyList<AgentDescriptor> Agents => [];
        public void ValidateFolder(string folder) => new Assembly.Infrastructure.Agents.AdapterLauncher([], "", new UnclaimedPermissions()).ValidateFolder(folder);
        public bool CanRunTerminalAuthentication => false;
        public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
            StageTimes stages, CancellationToken cancellationToken) => LaunchAsync(descriptor.Id, stages, cancellationToken);
        public Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
            Assembly.Application.Common.Acp.IPermissionPolicy policy, StageTimes stages, CancellationToken cancellationToken) =>
            LaunchAsync(descriptor.Id, stages, cancellationToken);

        public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken) => throw new InvalidOperationException("No adapter during migration");
    }

    private static void ProjectionSchema(IWriteScope scope) => SessionStore.EnsureEngineProjectionSchema(scope);
    private static string Initialize(IWriteScope scope, Action<IWriteScope>? projection = null) => EngineSchema.Initialize(scope, 10000, projection).Machine;

    private sealed class FaultScope(IWriteScope inner, string failAfter) : IWriteScope
    {
        public int Execute(string sql, params SqlArg[] args)
        {
            var changed = inner.Execute(sql, args);
            if (sql.Contains(failAfter, StringComparison.Ordinal)) throw new IOException("injected initialization failure");
            return changed;
        }
        public void AfterCommit(Action publishState) => inner.AfterCommit(publishState);
        public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args) => inner.Query(sql, map, args);
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args) => inner.QueryFirst(sql, map, args);
        public void Publish<T>(string topic, string? key, T change, JsonTypeInfo<T> type, bool keyedOnly = false) => inner.Publish(topic, key, change, type, keyedOnly);
    }

    private sealed class OldDb : IDisposable
    {
        private readonly TempDatabase _file = new();
        public string Path => _file.Path;
        public Topics Topics { get; } = new();
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(10000));
        public AppDatabase Db { get; }
        public OldDb(int version)
        {
            Db = AppDatabase.Open(_file.Path, Time, Topics); RowStore.Open(Db);
            Db.Writer.Write("test.frozen-producer", scope => scope.Execute(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", $"old-v{version}.sql"))));
        }
        public long Number(string sql) => Db.QueryFirst(sql, r => r.Long(0), default);
        public string? Text(string sql) => Db.QueryFirst(sql, r => r.IsNull(0) ? null : r.Text(0), default);
        public Dictionary<string, string[]> Columns() => Db.Query("SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'engine_%' ORDER BY name", r => r.Text(0), default)
            .ToDictionary(table => table, table => Db.Query($"PRAGMA table_info([{table}])", r => r.Text(1), default)
                .Where(c => table != "engine_meta" || c != "schema_version").ToArray());
        public string History(Dictionary<string, string[]> columns) => string.Join("", columns.Select(pair => Rows($"SELECT rowid,{string.Join(',', pair.Value.Select(c => '[' + c + ']'))} FROM [{pair.Key}] ORDER BY rowid")));
        public string Indices() => Rows("SELECT name,sql FROM sqlite_schema WHERE type='index' AND tbl_name='engine_session' ORDER BY name");
        public string ForeignKeys() => JsonSerializer.Serialize(Columns().Keys.SelectMany(table => Db.Query($"PRAGMA foreign_key_list([{table}])",
            r => new { Table = table, Target = r.Text(2), From = r.Text(3), To = r.Text(4) }, default))
            .Where(key => (key.Table == "engine_session" || key.Target == "engine_session") &&
                !(key.Table == "engine_needs_me" && key.From == "addressee_session_id")));
        public string Snapshot() => Rows("SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY name") + History(Columns());
        private string Rows(string sql)
        {
            using var connection = new SqliteConnection("Data Source=" + _file.Path); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader(); List<object?[]> rows = [];
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return JsonSerializer.Serialize(rows);
        }
        public void Dispose() { Db.Dispose(); _file.Dispose(); }
    }
}
