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

    // rows_summary holds only the kinds a summary reads, so deltas, messages and tools pay nothing for it.
    public const string Create = $"""
        CREATE TABLE IF NOT EXISTS sessions(session_id TEXT PRIMARY KEY, next_seq INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS rows(session_id TEXT NOT NULL, seq INTEGER NOT NULL, run_id TEXT, item_id TEXT,
          kind TEXT NOT NULL, payload TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (session_id, seq));
        CREATE INDEX IF NOT EXISTS rows_item ON rows(session_id, item_id, kind, seq);
        CREATE INDEX IF NOT EXISTS rows_summary ON rows(session_id, kind, item_id, seq) WHERE {SummaryKinds};
        CREATE TABLE IF NOT EXISTS tool_details(session_id TEXT NOT NULL, item_id TEXT NOT NULL, input TEXT, output TEXT,
          diff TEXT, path TEXT, PRIMARY KEY (session_id, item_id));
        """;

    /// <summary>
    /// Every session's newest turn row and the newest row of each of its asks, tasks and subagents, in session and seq
    /// order (<see cref="SessionSummaries"/>).
    /// </summary>
    public const string SummaryRows = $"""
        SELECT session_id, seq, run_id, item_id, kind, payload, created_at FROM rows WHERE (session_id, seq) IN (
          SELECT session_id, MAX(seq) FROM rows INDEXED BY rows_summary WHERE {SummaryKinds}
          GROUP BY session_id, kind, CASE WHEN kind = 'turn' THEN NULL ELSE item_id END)
        ORDER BY session_id, seq;
        """;

    /// <summary>
    /// The seq of the session's newest turn row that ended a turn, if any. Turn rows have no item, so ordering by item
    /// then seq is the index's own order: it walks them back from the newest and stops at the first end, however many
    /// turns the session has.
    /// </summary>
    public const string LastTurnEnd = $"""
        SELECT seq FROM rows INDEXED BY rows_summary WHERE session_id = $session AND {SummaryKinds} AND kind = 'turn'
          AND json_extract(payload, '$.state') IN ('finished', 'failed', 'cancelled')
        ORDER BY item_id DESC, seq DESC LIMIT 1;
        """;

    // A real resend comes within seconds, or soon after a restart, so only the newest user rows can hold its clientId.
    private const string ClientIdRows = "200";

    /// <summary>
    /// The run and text of the newest of the session's last <see cref="ClientIdRows"/> user rows sent with clientId
    /// <c>$client</c>. A user row has no item, so rows_item finds the session's user rows and walks them newest first.
    /// </summary>
    public const string UserRowOfClient = $"""
        SELECT run_id, json_extract(payload, '$.text') FROM (
          SELECT run_id, payload, seq FROM rows INDEXED BY rows_item
          WHERE session_id = $session AND item_id IS NULL AND kind = 'user' ORDER BY seq DESC LIMIT {ClientIdRows})
        WHERE json_extract(payload, '$.clientId') = $client ORDER BY seq DESC LIMIT 1;
        """;

    /// <summary>
    /// The rail's read (<see cref="Sessions.SessionStore.Rail"/>). <c>parents</c> are the open top-level sessions the
    /// filters pick, with each one's project label ("owner/repo" for a GitHub URL, else the folder's last name).
    /// <c>family</c> is each of them and its children, with each one's newest turn state and first open ask, both read
    /// through rows_summary. <c>tops</c> folds a family into its parent: it runs or needs you if any of them does.
    /// <c>listed</c> is the tab's parents, <c>page</c> its page after the cursor, A to Z by label then repo, newest
    /// first; <c>helpers</c> is the expanded parent's page of children. Every row has the same columns: part (0
    /// totals, 1 a provider, 2 a parent, 3 a helper), its order, session, agent, repo, label, group start, title,
    /// machine, updated, completed, running, needs you, failed, unread, helpers, ask session, ask seq, branch and when
    /// it was recorded. The totals row puts All in the helpers column and the other two in theirs.
    /// </summary>
    public const string Rail = $"""
        WITH parents AS (
          SELECT session_id, agent, repo_url, title, machine, updated_at, completed_at, read_seq,
            CASE WHEN repo_url LIKE 'https://github.com/%' THEN rtrim(substr(repo_url, 20), '/')
                 ELSE substr(rtrim(repo_url, '/'), length(rtrim(rtrim(repo_url, '/'), replace(rtrim(repo_url, '/'), '/', ''))) + 1) END AS label
          FROM session_info
          WHERE parent_session_id IS NULL AND settled_at IS NULL
            AND ($providers IS NULL OR agent IN (SELECT value FROM json_each($providers)))
            AND ($locations IS NULL OR iif(machine IS NULL, 'local', 'remote') IN (SELECT value FROM json_each($locations)))
            AND ($filter IS NULL OR instr(lower(COALESCE(title, '') || char(10) || repo_url), lower($filter)) > 0)),
        family AS (
          SELECT COALESCE(s.parent_session_id, s.session_id) AS top, s.session_id, s.agent, s.repo_url, s.title, s.machine, s.updated_at, s.completed_at,
            (SELECT json_extract(t.payload, '$.state') FROM rows t INDEXED BY rows_summary
             WHERE t.session_id = s.session_id AND {SummaryKinds} AND t.kind = 'turn' ORDER BY t.item_id DESC, t.seq DESC LIMIT 1) AS turn,
            (SELECT MIN(a.seq) FROM rows a INDEXED BY rows_summary
             WHERE a.session_id = s.session_id AND {SummaryKinds} AND a.kind = 'ask' AND json_extract(a.payload, '$.state') = 'open'
               AND NOT EXISTS (SELECT 1 FROM rows b INDEXED BY rows_summary WHERE b.session_id = a.session_id AND {SummaryKinds}
                               AND b.kind = 'ask' AND b.item_id = a.item_id AND b.seq > a.seq)) AS ask_seq,
            EXISTS (SELECT 1 FROM rows w INDEXED BY rows_summary
             WHERE w.session_id = s.session_id AND {SummaryKinds} AND w.kind IN ('task', 'subagent')
               AND json_extract(w.payload, '$.state') IN ('running', 'requires_action')
               AND NOT EXISTS (SELECT 1 FROM rows l INDEXED BY rows_summary WHERE l.session_id = w.session_id AND {SummaryKinds}
                               AND l.kind = w.kind AND l.item_id = w.item_id AND l.seq > w.seq)) AS busy
          FROM session_info s WHERE COALESCE(s.parent_session_id, s.session_id) IN (SELECT session_id FROM parents)),
        tops AS (
          SELECT top, MAX(turn IS 'started' OR busy) AS running, MAX(ask_seq IS NOT NULL) AS needs,
            MAX(session_id = top AND turn IS 'failed') AS failed, COUNT(*) - 1 AS helpers
          FROM family GROUP BY top),
        asks AS (
          SELECT top, session_id, ask_seq, ROW_NUMBER() OVER (PARTITION BY top ORDER BY session_id <> top, ask_seq, session_id) AS n
          FROM family WHERE ask_seq IS NOT NULL),
        listed AS (
          SELECT p.*, t.running, t.needs, t.failed, t.helpers,
            ROW_NUMBER() OVER (PARTITION BY p.repo_url ORDER BY p.updated_at DESC, p.session_id DESC) = 1 AS group_start
          FROM parents p JOIN tops t ON t.top = p.session_id
          WHERE $tab = 'all' OR ($tab = 'running' AND t.running) OR ($tab = 'needs' AND t.needs)),
        ranked AS (SELECT *, ROW_NUMBER() OVER (ORDER BY lower(label), repo_url, updated_at DESC, session_id DESC) AS n FROM listed),
        page AS (
          SELECT n, session_id, agent, repo_url, label, group_start, title, machine, updated_at, completed_at, read_seq,
            running, needs, failed, helpers FROM ranked
          WHERE CASE WHEN $anchor IN (SELECT session_id FROM ranked) THEN n >= (SELECT n FROM ranked WHERE session_id = $anchor) - $above
            ELSE $group IS NULL OR (lower(label), repo_url) > (lower($group), $repo)
              OR ((lower(label), repo_url) = (lower($group), $repo) AND (updated_at, session_id) < ($updated, $id)) END
          ORDER BY n LIMIT $limit),
        kids AS (
          SELECT *, ROW_NUMBER() OVER (ORDER BY updated_at DESC, session_id DESC) AS n FROM family WHERE top = $expanded AND session_id <> top),
        helpers AS (
          SELECT n, session_id, agent, repo_url, title, machine, updated_at, completed_at, turn, busy, ask_seq FROM kids
          WHERE CASE WHEN $helperAnchor IN (SELECT session_id FROM kids) THEN n >= (SELECT n FROM kids WHERE session_id = $helperAnchor) - $helperAbove
            ELSE $helperUpdated IS NULL OR (updated_at, session_id) < ($helperUpdated, $helperId) END
          ORDER BY n LIMIT $helperLimit)
        SELECT 0, 0, NULL, NULL, NULL, NULL, 0, NULL, NULL, 0, NULL, COALESCE(SUM(running), 0), COALESCE(SUM(needs), 0), 0, 0, COUNT(*),
          NULL, NULL, NULL, NULL FROM tops
        UNION ALL
        SELECT 1, ROW_NUMBER() OVER (ORDER BY agent), NULL, agent, NULL, NULL, 0, NULL, NULL, 0, NULL, 0, 0, 0, 0, 0, NULL, NULL, NULL, NULL
        FROM (SELECT DISTINCT agent FROM session_info WHERE parent_session_id IS NULL AND settled_at IS NULL)
        UNION ALL
        SELECT 2, p.n, p.session_id, p.agent, p.repo_url, p.label, p.group_start, p.title, p.machine, p.updated_at, p.completed_at,
          p.running, p.needs, p.failed,
          COALESCE((SELECT e.seq FROM rows e INDEXED BY rows_summary WHERE e.session_id = p.session_id AND {SummaryKinds} AND e.kind = 'turn'
                      AND json_extract(e.payload, '$.state') IN ('finished', 'failed', 'cancelled') ORDER BY e.item_id DESC, e.seq DESC LIMIT 1), 0) > p.read_seq,
          p.helpers, a.session_id, a.ask_seq, json_extract(b.payload, '$.branch'), b.created_at
        FROM page p
        LEFT JOIN asks a ON a.top = p.session_id AND a.n = 1
        LEFT JOIN rows b ON b.session_id = p.session_id AND b.seq = (
          SELECT MAX(r.seq) FROM rows r INDEXED BY rows_item WHERE r.session_id = p.session_id AND r.item_id IS NULL AND r.kind = 'stats'
            AND COALESCE(json_extract(r.payload, '$.branch'), '') <> '')
        UNION ALL
        SELECT 3, h.n, h.session_id, h.agent, h.repo_url, '', 0, h.title, h.machine, h.updated_at, h.completed_at, h.turn IS 'started' OR h.busy,
          h.ask_seq IS NOT NULL, h.turn IS 'failed', 0, 0, iif(h.ask_seq IS NULL, NULL, h.session_id), h.ask_seq, NULL, NULL
        FROM helpers h
        ORDER BY 1, 2;
        """;

    // The kinds a summary reads. SQLite uses a partial index only for a query that names its condition as the index
    // does, so the queries use this text as it is; INDEXED BY makes a query that drifts from it fail to prepare.
    private const string SummaryKinds = "kind IN ('turn', 'ask', 'task', 'subagent')";

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
