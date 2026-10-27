using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Application.Features.Chat;

/// <summary>The row store's SQL. Kept apart so the queries can be read on their own.</summary>
internal static class Schema
{
    // Sessions were called threads until the rename; a database from then has these names.
    private static readonly (string Old, string New)[] OldTables =
        [("threads", "sessions"), ("thread_info", "session_info"), ("thread_config", "session_config"), ("thread_commands", "session_commands")];

    private static readonly (string Table, string Old, string New)[] OldColumns =
    [
        ("sessions", "thread_id", "session_id"), ("rows", "thread_id", "session_id"), ("tool_details", "thread_id", "session_id"),
        ("session_info", "thread_id", "session_id"), ("session_info", "parent_thread_id", "parent_session_id"),
        ("session_config", "thread_id", "session_id"), ("session_commands", "thread_id", "session_id"),
        ("helper_messages", "from_thread", "from_session"), ("helper_messages", "to_thread", "to_session"),
    ];

    /// <summary>
    /// Renames a database from before the rename, in the write that opens it, before its tables are created: the old
    /// tables and columns get the session names, a saved subagent's child id moves from <c>threadId</c> to
    /// <c>sessionId</c>, and the old updated_at index goes so the new one replaces it. A database that has none of the
    /// old names is left as it is.
    /// </summary>
    public static void RenameOldNames(IWriteScope scope)
    {
        bool Has(string sql, params SqlArg[] args) => scope.QueryFirst(sql, row => row.Long(0), args) > 0;
        const string Table = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        var oldRows = Has("SELECT COUNT(*) FROM pragma_table_info('rows') WHERE name = 'thread_id';");
        foreach (var (old, renamed) in OldTables)
            // An older build that opened the renamed database made the old tables again, empty: they're left alone.
            if (Has(Table, new SqlArg("$name", old)) && !Has(Table, new SqlArg("$name", renamed)))
                scope.Execute($"ALTER TABLE {old} RENAME TO {renamed};");
        foreach (var (table, old, renamed) in OldColumns)
            if (Has("SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $name;", new SqlArg("$table", table), new SqlArg("$name", old)))
                scope.Execute($"ALTER TABLE {table} RENAME COLUMN {old} TO {renamed};");
        if (oldRows)
            scope.Execute(
                """
                UPDATE rows SET payload = json_remove(json_set(payload, '$.sessionId', json_extract(payload, '$.threadId')), '$.threadId')
                WHERE kind = 'subagent' AND json_type(payload, '$.threadId') IS NOT NULL;
                """);
        scope.Execute("DROP INDEX IF EXISTS thread_info_updated;");
    }

    public const string Create = """
        CREATE TABLE IF NOT EXISTS sessions(session_id TEXT PRIMARY KEY, next_seq INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS rows(session_id TEXT NOT NULL, seq INTEGER NOT NULL, run_id TEXT, item_id TEXT,
          kind TEXT NOT NULL, payload TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (session_id, seq));
        CREATE INDEX IF NOT EXISTS rows_item ON rows(session_id, item_id, kind, seq);
        CREATE TABLE IF NOT EXISTS tool_details(session_id TEXT NOT NULL, item_id TEXT NOT NULL, input TEXT, output TEXT,
          diff TEXT, path TEXT, PRIMARY KEY (session_id, item_id));
        """;

    /// <summary>
    /// One page, in one statement, reading only the page's own items. <c>picked</c> is the newest <c>$limit + 1</c>
    /// items below <c>$before</c>: walking the session back from <c>$before</c>, each item's first row (or a row with
    /// no item, which is its own). <c>shown</c> is each picked item's newest row of each kind, leaving out a message's
    /// deltas once it has its final row; <c>first_seq</c> is where the item sits. <c>CROSS JOIN</c> keeps
    /// <c>picked</c> outside, so SQLite reads its items rather than the whole session. For a delta, column 8 is all
    /// its item's delta text, so the page can draw a message that's still streaming. Column 9 is how many items were
    /// picked.
    /// </summary>
    public const string PageBefore = """
        WITH picked AS (
          SELECT r.seq AS first_seq, r.item_id FROM rows r
          WHERE r.session_id = $session AND r.seq < $before
            AND (r.item_id IS NULL OR NOT EXISTS (SELECT 1 FROM rows f INDEXED BY rows_item
                 WHERE f.session_id = r.session_id AND f.item_id = r.item_id AND f.seq < r.seq))
          ORDER BY r.seq DESC LIMIT $limit + 1),
        shown AS (
          SELECT r.session_id, r.seq, r.run_id, r.item_id, r.kind, r.payload, r.created_at, p.first_seq
          FROM picked p CROSS JOIN rows r ON r.session_id = $session AND r.item_id = p.item_id
          WHERE NOT (r.kind = 'delta' AND EXISTS (SELECT 1 FROM rows m WHERE m.session_id = r.session_id
                     AND m.item_id = r.item_id AND m.kind = 'message'))
            AND NOT EXISTS (SELECT 1 FROM rows l WHERE l.session_id = r.session_id AND l.item_id = r.item_id
                            AND l.kind = r.kind AND l.seq > r.seq)
          UNION ALL
          SELECT r.session_id, r.seq, r.run_id, r.item_id, r.kind, r.payload, r.created_at, p.first_seq
          FROM picked p CROSS JOIN rows r ON r.session_id = $session AND r.seq = p.first_seq
          WHERE p.item_id IS NULL)
        SELECT p.session_id, p.seq, p.run_id, p.item_id, p.kind, p.payload, p.created_at, p.first_seq,
          CASE WHEN p.kind = 'delta' THEN (SELECT group_concat(json_extract(d.payload, '$.text'), '' ORDER BY d.seq)
                                           FROM rows d WHERE d.session_id = p.session_id AND d.item_id = p.item_id AND d.kind = 'delta') END,
          (SELECT COUNT(*) FROM picked)
        FROM shown p
        ORDER BY p.first_seq, p.seq;
        """;
}
