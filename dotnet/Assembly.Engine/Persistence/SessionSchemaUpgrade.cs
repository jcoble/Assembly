using Assembly.Application.Common.Database;

namespace Assembly.Engine.Persistence;

internal static class SessionSchemaUpgrade
{
    private static readonly string[] Columns = ["id", "version", "machine", "created_ms", "updated_ms", "task_url", "provider", "model", "folder", "title",
        "state", "activity_state", "parent_id", "run_id", "provider_session_id", "holder", "epoch", "reserved_epoch", "cancel_requested", "lost_ms", "can_steer"];

    public static void Apply(IWriteScope scope, Action<IWriteScope>? projectionSchema)
    {
        var version = scope.QueryFirst("SELECT sqlite_version()", r => r.Text(0));
        if (!Version.TryParse(version, out var sqlite) || sqlite < new Version(3, 35))
            throw new InvalidOperationException($"Engine session migration requires SQLite 3.35.0 or newer; found {version}.");
        if (scope.QueryFirst("PRAGMA foreign_keys", r => r.Long(0)) != 1 || scope.QueryFirst("PRAGMA writable_schema", r => r.Long(0)) != 0)
            throw new InvalidOperationException("Engine session migration requires foreign_keys ON and writable_schema OFF.");
        var columns = scope.Query("PRAGMA table_info(engine_session)", r => new { Name = r.Text(1), Type = r.Text(2), Required = r.Long(3) });
        if (!columns.Select(c => c.Name).Order().SequenceEqual(Columns.Order()) ||
            columns.Single(c => c.Name == "model") is not { Type: "TEXT", Required: 1 })
            throw new InvalidOperationException("Unsupported v3 engine_session column shape.");
        var indices = scope.Query("PRAGMA index_list(engine_session)", r => r.Text(1));
        if (!indices.Order().SequenceEqual(new[] { "engine_session_parent_state", "engine_session_run_state", "sqlite_autoindex_engine_session_1" }.Order()) ||
            !scope.Query("PRAGMA index_info(engine_session_parent_state)", r => r.Text(2)).SequenceEqual(["parent_id", "state"]) ||
            !scope.Query("PRAGMA index_info(engine_session_run_state)", r => r.Text(2)).SequenceEqual(["run_id", "state"]))
            throw new InvalidOperationException("Unsupported v3 engine_session index dependencies.");
        var incoming = scope.Query("""
            SELECT m.name,f."from",f."to" FROM sqlite_schema m JOIN pragma_foreign_key_list(m.name) f
            WHERE m.type='table' AND f."table"='engine_session' ORDER BY m.name,f."from"
            """, r => (Key: r.Text(0) + "." + r.Text(1), Target: r.Text(2)));
        string[] expected = ["engine_session.parent_id", "engine_run.owner_session_id", "engine_step.session_id", "engine_needs_me.session_id",
            "engine_message.from_session_id", "engine_message.to_session_id", "engine_handoff.session_id", "engine_tool_call.session_id", "engine_usage.session_id", "engine_outbox.session_id"];
        if (!incoming.Select(k => k.Key).Order().SequenceEqual(expected.Order()) || incoming.Any(k => k.Target != "id"))
            throw new InvalidOperationException("Unsupported v3 engine_session foreign-key dependencies.");

        scope.Execute("ALTER TABLE engine_session ADD COLUMN model_v4 TEXT NULL");
        scope.Execute("UPDATE engine_session SET model_v4=model");
        scope.Execute("ALTER TABLE engine_session DROP COLUMN model");
        scope.Execute("ALTER TABLE engine_session RENAME COLUMN model_v4 TO model");
        scope.Execute("ALTER TABLE engine_session ADD COLUMN account_context TEXT NOT NULL DEFAULT ''");
        scope.Execute("ALTER TABLE engine_session ADD COLUMN managed_checkout INTEGER NOT NULL DEFAULT 0 CHECK(managed_checkout IN (0,1))");
        projectionSchema?.Invoke(scope);
        if (scope.Query("PRAGMA foreign_key_check", r => r.Text(0)).Count != 0)
            throw new InvalidOperationException("Engine session migration found invalid foreign keys.");
        scope.Execute("UPDATE engine_meta SET schema_version=4 WHERE singleton=1");
    }
}
