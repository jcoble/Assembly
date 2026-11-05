using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
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
          diff TEXT, path TEXT, output_source TEXT, acp_session_id TEXT, PRIMARY KEY (session_id, item_id));
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
    /// The rail's sessions, own state, ordering, filters, totals and page, in one SQL statement. A session's row ends with
    /// its preview: its newest assistant message as plain text, its first 160 characters (each block's text parts run
    /// together, blocks joined by a space).
    /// </summary>
    public static readonly string Rail = $"""
        WITH local_source AS (
          SELECT machine_id FROM engine_meta WHERE singleton=1),
        source_candidates AS (
          SELECT s.session_id, local.machine_id AS source_machine, 0 AS source_generation, NULL AS source_route
          FROM session_info s CROSS JOIN local_source local
          JOIN engine_session known ON known.id=s.session_id AND known.machine=local.machine_id
            AND known.holder='local' AND known.provider=s.agent AND known.parent_id IS s.parent_session_id
          WHERE s.machine IS NULL AND NOT EXISTS (
            SELECT 1 FROM engine_session other WHERE other.id=s.session_id AND other.machine<>local.machine_id)
          UNION ALL
          SELECT s.session_id, m.machine, m.generation, m.name
          FROM session_info s JOIN engine_mirror m ON m.name=s.machine AND m.state='live'
          JOIN engine_mirror_row owner ON owner.machine=m.machine AND owner.generation=m.generation
            AND owner.entity_kind='session' AND owner.entity_id=s.session_id
          WHERE m.generation>0 AND length(m.machine)>0 AND length(m.name)>0
            AND json_type(owner.row_json,'$.id')='text' AND json_type(owner.row_json,'$.machine')='text'
            AND json_extract(owner.row_json,'$.id')=s.session_id AND json_extract(owner.row_json,'$.machine')=m.machine
            AND json_extract(owner.row_json,'$.holder')='local' AND json_extract(owner.row_json,'$.state')<>'foreign'
            AND json_type(owner.row_json,'$.provider')='text' AND json_extract(owner.row_json,'$.provider')=s.agent
            AND json_extract(owner.row_json,'$.parentId') IS s.parent_session_id
            AND EXISTS (SELECT 1 FROM engine_session known WHERE known.id=s.session_id AND known.machine=m.machine
              AND known.holder<>'local' AND known.provider=s.agent AND known.parent_id IS s.parent_session_id)
            AND NOT EXISTS (SELECT 1 FROM engine_session other WHERE other.id=s.session_id AND other.machine<>m.machine)),
        rail_sources AS (
          SELECT DISTINCT session_id, source_machine, source_generation, source_route FROM source_candidates),
        canonical_sessions AS (
          SELECT e.id AS session_id, e.machine AS source_machine, 0 AS source_generation, NULL AS source_route,
            e.provider AS agent, e.folder, e.title, e.updated_ms AS updated_at, e.parent_id,
            {Assembly.Application.Common.Engine.BareSessionFolder.Predicate("e.project_id", "e.folder")} AS is_bare
          FROM engine_session e JOIN local_source local ON local.machine_id=e.machine
          WHERE e.holder='local' AND e.state<>'foreign'
          UNION ALL
          SELECT e.entity_id, e.machine, e.generation, m.name,
            json_extract(e.row_json,'$.provider'), json_extract(e.row_json,'$.folder'),
            CASE WHEN json_type(e.row_json,'$.title')='text' THEN json_extract(e.row_json,'$.title') END,
            json_extract(e.row_json,'$.updatedMs'),
            CASE WHEN json_type(e.row_json,'$.parentId')='text' THEN json_extract(e.row_json,'$.parentId') END,
            json_extract(e.row_json,'$.isBare')=1 AND json_extract(e.row_json,'$.projectId') IS NULL
          FROM engine_mirror_row e JOIN engine_mirror m ON m.machine=e.machine AND m.generation=e.generation AND m.state='live'
          WHERE e.entity_kind='session' AND m.generation>0 AND length(m.machine)>0 AND length(m.name)>0
            AND json_type(e.row_json,'$.id')='text' AND json_extract(e.row_json,'$.id')=e.entity_id
            AND json_type(e.row_json,'$.machine')='text' AND json_extract(e.row_json,'$.machine')=m.machine
            AND json_extract(e.row_json,'$.holder')='local' AND json_extract(e.row_json,'$.state')<>'foreign'
            AND json_type(e.row_json,'$.provider')='text' AND json_type(e.row_json,'$.folder')='text'
            AND json_type(e.row_json,'$.updatedMs')='integer'),
        rail_roster AS (
          SELECT c.session_id, c.agent,
            CASE WHEN c.source_route IS NULL THEN COALESCE(s.repo_url,c.folder) ELSE c.folder END AS repo_url,
            COALESCE(s.title,c.title) AS title, c.source_route AS machine, COALESCE(s.updated_at,c.updated_at) AS updated_at, s.completed_at, s.settled_at, COALESCE(s.read_seq,0) AS read_seq,
            c.source_machine, c.source_generation, c.source_route,
            s.pinned_at, s.orchestrator_at, s.position, s.session_id AS facade_id, COALESCE(c.is_bare,0) AS is_bare
          FROM canonical_sessions c LEFT JOIN session_info s ON s.session_id=c.session_id AND s.machine IS c.source_route
            AND s.agent=c.agent AND s.parent_session_id IS c.parent_id
            AND (c.source_route IS NULL OR EXISTS (SELECT 1 FROM engine_session known
              WHERE known.id=c.session_id AND known.machine=c.source_machine AND known.holder<>'local'
                AND known.provider=s.agent AND known.parent_id IS s.parent_session_id))
            AND NOT EXISTS (SELECT 1 FROM engine_session other WHERE other.id=s.session_id AND other.machine<>c.source_machine)
          WHERE c.parent_id IS NULL OR s.helper=1
          UNION ALL
          SELECT s.session_id, s.agent, s.repo_url, s.title, s.machine, s.updated_at, s.completed_at, s.settled_at, s.read_seq,
            source.source_machine, source.source_generation, source.source_route, s.pinned_at, s.orchestrator_at, s.position, s.session_id, s.is_bare AND s.project_id IS NULL
          FROM session_info s LEFT JOIN rail_sources source ON source.session_id=s.session_id AND source.source_route IS s.machine
          WHERE (s.parent_session_id IS NULL OR s.helper=1)
            AND NOT EXISTS (SELECT 1 FROM canonical_sessions c WHERE c.session_id=s.session_id
              AND c.source_machine=source.source_machine AND c.source_generation=source.source_generation
              AND c.source_route IS source.source_route AND c.agent=s.agent AND c.parent_id IS s.parent_session_id)),
        engine_needs AS (
          SELECT n.id, n.machine, 0 AS generation, n.session_id
          FROM engine_needs_me n JOIN local_source local ON local.machine_id=n.machine WHERE n.state='open' AND {OwnerAttention.Need}
          UNION ALL
          SELECT t.id, t.machine, 0, r.owner_session_id
          FROM engine_run r JOIN engine_step t ON t.run_id=r.id JOIN local_source local ON local.machine_id=t.machine
          WHERE r.state IN ('queued','running','needs_me') AND {OwnerAttention.Step}
          UNION ALL
          SELECT n.entity_id, n.machine, n.generation, json_extract(n.row_json,'$.sessionId')
          FROM engine_mirror_row n JOIN engine_mirror m ON m.machine=n.machine AND m.generation=n.generation AND m.state='live'
          WHERE n.entity_kind='needs_me' AND json_type(n.row_json,'$.id')='text' AND json_type(n.row_json,'$.machine')='text'
            AND json_type(n.row_json,'$.sessionId')='text' AND json_extract(n.row_json,'$.id')=n.entity_id
            AND json_extract(n.row_json,'$.machine')=n.machine AND json_extract(n.row_json,'$.state')='open'
            AND json_extract(n.row_json,'$.forOwner')=1
          UNION ALL
          SELECT n.entity_id, n.machine, n.generation, json_extract(n.row_json,'$.ownerSessionId')
          FROM engine_mirror_row n JOIN engine_mirror m ON m.machine=n.machine AND m.generation=n.generation AND m.state='live'
          WHERE n.entity_kind='step' AND json_extract(n.row_json,'$.needsOwner')=1),
        sessions AS (
          SELECT s.session_id, s.agent,
            CASE WHEN s.is_bare THEN 'bare:' || COALESCE(s.machine,'local') ELSE s.repo_url END AS repo_url,
            CASE WHEN s.is_bare THEN 'No project'
                 WHEN s.repo_url LIKE 'https://github.com/%' THEN rtrim(substr(s.repo_url, 20), '/')
                 ELSE substr(rtrim(s.repo_url, '/'), length(rtrim(rtrim(s.repo_url, '/'), replace(rtrim(s.repo_url, '/'), '/', ''))) + 1) END AS label,
            CASE WHEN s.settled_at IS NOT NULL THEN 'settled' WHEN s.completed_at IS NOT NULL THEN 'done' ELSE 'working' END AS section,
            CASE WHEN s.settled_at IS NOT NULL THEN 2 WHEN s.completed_at IS NOT NULL THEN 1 ELSE 0 END AS section_index,
            s.title, s.machine, s.updated_at, s.completed_at, s.read_seq,
            s.source_machine, s.source_generation, s.source_route, s.facade_id
          FROM rail_roster s
          WHERE ($providers IS NULL OR s.agent IN (SELECT value FROM json_each($providers)))
            AND ($locations IS NULL OR iif(s.machine IS NULL, 'local', 'remote') IN (SELECT value FROM json_each($locations)))
            AND ($statuses IS NULL OR (CASE WHEN s.settled_at IS NOT NULL THEN 'settled' WHEN s.completed_at IS NOT NULL THEN 'done' ELSE 'working' END)
                   IN (SELECT value FROM json_each($statuses)))
            AND ($filter IS NULL OR instr(lower(COALESCE(s.title, '') || char(10) || CASE WHEN s.is_bare THEN 'No project' ELSE s.repo_url END), lower($filter)) > 0)),
        totals AS (
          SELECT COUNT(*) AS needs FROM sessions p JOIN engine_needs n ON n.session_id=p.session_id
            AND n.machine=p.source_machine AND n.generation=p.source_generation),
        section_headers AS (
          SELECT 'section:' || section AS session_id, 'section' AS kind, section, '' AS agent, '' AS repo_url,
            CASE section WHEN 'working' THEN 'Working' WHEN 'done' THEN 'Done' ELSE 'Settled' END AS group_label,
            COUNT(*) AS count, NULL AS machines, NULL AS title, NULL AS machine, 0 AS updated_at, NULL AS completed_at,
            0 AS read_seq, section_index, '' AS order_group, '' AS order_repo, 0 AS header, '' AS name,
            NULL AS source_machine, NULL AS source_generation, NULL AS source_route, NULL AS facade_id
          FROM sessions WHERE $status_group = 1 GROUP BY section, section_index),
        project_headers AS (
          SELECT 'project:' || iif($status_group = 1, section, '') || '::' || repo_url AS session_id,
            'project' AS kind, iif($status_group = 1, section, '') AS section, '' AS agent, repo_url, label AS group_label,
            COUNT(*) AS count, group_concat(DISTINCT coalesce(machine, 'local')) AS machines, NULL AS title, NULL AS machine,
            0 AS updated_at, NULL AS completed_at, 0 AS read_seq,
            iif($status_group = 1, section_index, 0) AS section_index, label AS order_group, repo_url AS order_repo, 1 AS header,
            '' AS name, NULL AS source_machine, NULL AS source_generation, NULL AS source_route, NULL AS facade_id
          FROM sessions WHERE $project_group = 1
            AND NOT ($status_group = 1 AND section IN (SELECT value FROM json_each($folded)))
          GROUP BY iif($status_group = 1, section, ''), iif($status_group = 1, section_index, 0), repo_url, label),
        members AS (
          SELECT session_id, 'session' AS kind, section, agent, repo_url, label AS group_label, 0 AS count, NULL AS machines,
            title, machine, updated_at, completed_at, read_seq,
            iif($status_group = 1, section_index, 0) AS section_index,
            iif($project_group = 1, label, '') AS order_group, iif($project_group = 1, repo_url, '') AS order_repo,
            2 AS header, lower(COALESCE(NULLIF(trim(title), ''), label)) AS name,
            source_machine, source_generation, source_route, facade_id
          FROM sessions p
          WHERE NOT ($status_group = 1 AND section IN (SELECT value FROM json_each($folded)))
            AND NOT ($project_group = 1 AND (iif($status_group = 1, section, '') || '::' || repo_url)
              IN (SELECT value FROM json_each($folded)))),
        entries AS (
          SELECT {RailColumns} FROM section_headers UNION ALL SELECT {RailColumns} FROM project_headers
          UNION ALL SELECT {RailColumns} FROM members),
        ordered AS (
          SELECT *, json_array(updated_at, name, session_id, source_machine, source_generation, source_route) AS ordering,
            ROW_NUMBER() OVER (ORDER BY section_index,
              CASE WHEN header = 0 THEN 0 ELSE 1 END,
              CASE WHEN order_repo = '' THEN 1 ELSE 0 END, lower(order_group), order_repo, header,
              CASE WHEN header = 2 AND $sort = 'recent' AND $dir = 'asc' THEN updated_at END ASC,
              CASE WHEN header = 2 AND $sort = 'recent' AND $dir = 'desc' THEN updated_at END DESC,
              CASE WHEN header = 2 AND $sort = 'name' AND $dir = 'asc' THEN name END ASC,
              CASE WHEN header = 2 AND $sort = 'name' AND $dir = 'desc' THEN name END DESC,
              CASE WHEN header = 2 AND $dir = 'asc' THEN session_id END ASC,
              CASE WHEN header = 2 AND $dir = 'desc' THEN session_id END DESC,
              CASE WHEN $dir='asc' THEN COALESCE(source_machine,'') END ASC,
              CASE WHEN $dir='desc' THEN COALESCE(source_machine,'') END DESC,
              CASE WHEN $dir='asc' THEN COALESCE(source_generation,0) END ASC,
              CASE WHEN $dir='desc' THEN COALESCE(source_generation,0) END DESC,
              CASE WHEN $dir='asc' THEN COALESCE(source_route,'') END ASC,
              CASE WHEN $dir='desc' THEN COALESCE(source_route,'') END DESC) AS n
          FROM entries),
        candidates AS (
          SELECT {RailColumns}, ordering, n FROM ordered o
          WHERE CASE WHEN EXISTS (SELECT 1 FROM ordered WHERE session_id=$anchor AND ($after_ordering IS NULL OR (
              COALESCE(source_machine,'')=COALESCE(json_extract($after_ordering,'$[3]'),'')
              AND COALESCE(source_generation,0)=COALESCE(json_extract($after_ordering,'$[4]'),0)
              AND COALESCE(source_route,'')=COALESCE(json_extract($after_ordering,'$[5]'),''))))
            THEN n >= (SELECT MIN(n) FROM ordered WHERE session_id=$anchor AND ($after_ordering IS NULL OR (
              COALESCE(source_machine,'')=COALESCE(json_extract($after_ordering,'$[3]'),'')
              AND COALESCE(source_generation,0)=COALESCE(json_extract($after_ordering,'$[4]'),0)
              AND COALESCE(source_route,'')=COALESCE(json_extract($after_ordering,'$[5]'),'')))) - $above
            ELSE $after_section IS NULL
              OR (section_index, CASE WHEN header = 0 THEN 0 ELSE 1 END,
                  CASE WHEN order_repo = '' THEN 1 ELSE 0 END, lower(order_group), order_repo, header)
                 > ($after_section, CASE WHEN $after_header = 0 THEN 0 ELSE 1 END,
                    CASE WHEN $after_repo = '' THEN 1 ELSE 0 END, lower($after_group), $after_repo, $after_header)
              OR ((section_index, CASE WHEN header = 0 THEN 0 ELSE 1 END,
                   CASE WHEN order_repo = '' THEN 1 ELSE 0 END, lower(order_group), order_repo, header)
                  = ($after_section, CASE WHEN $after_header = 0 THEN 0 ELSE 1 END,
                     CASE WHEN $after_repo = '' THEN 1 ELSE 0 END, lower($after_group), $after_repo, $after_header)
                AND header = 2 AND (
                  ($sort = 'recent' AND ((
                    $dir = 'asc' AND (updated_at, session_id, COALESCE(source_machine,''), COALESCE(source_generation,0), COALESCE(source_route,'')) > (json_extract($after_ordering, '$[0]'), json_extract($after_ordering, '$[2]'), COALESCE(json_extract($after_ordering,'$[3]'),''), COALESCE(json_extract($after_ordering,'$[4]'),0), COALESCE(json_extract($after_ordering,'$[5]'),'')))
                    OR ($dir = 'desc' AND (updated_at, session_id, COALESCE(source_machine,''), COALESCE(source_generation,0), COALESCE(source_route,'')) < (json_extract($after_ordering, '$[0]'), json_extract($after_ordering, '$[2]'), COALESCE(json_extract($after_ordering,'$[3]'),''), COALESCE(json_extract($after_ordering,'$[4]'),0), COALESCE(json_extract($after_ordering,'$[5]'),'')))))
                  OR ($sort = 'name' AND ((
                    $dir = 'asc' AND (name, session_id, COALESCE(source_machine,''), COALESCE(source_generation,0), COALESCE(source_route,'')) > (json_extract($after_ordering, '$[1]'), json_extract($after_ordering, '$[2]'), COALESCE(json_extract($after_ordering,'$[3]'),''), COALESCE(json_extract($after_ordering,'$[4]'),0), COALESCE(json_extract($after_ordering,'$[5]'),'')))
                    OR ($dir = 'desc' AND (name, session_id, COALESCE(source_machine,''), COALESCE(source_generation,0), COALESCE(source_route,'')) < (json_extract($after_ordering, '$[1]'), json_extract($after_ordering, '$[2]'), COALESCE(json_extract($after_ordering,'$[3]'),''), COALESCE(json_extract($after_ordering,'$[4]'),0), COALESCE(json_extract($after_ordering,'$[5]'),''))))))) END
          ORDER BY n LIMIT $limit),
        page AS (
          SELECT {RailColumns}, ordering, n FROM ordered
          WHERE n IN (SELECT n FROM candidates)
            OR ($anchor IS NOT NULL AND NOT EXISTS (SELECT 1 FROM candidates))
          ORDER BY n LIMIT $limit),
        engine_sessions AS (
          SELECT e.id, e.machine, 0 AS generation, e.parent_id, e.model, r.worktree, e.activity_state, e.branch
          FROM engine_session e JOIN local_source local ON local.machine_id=e.machine
          JOIN page p ON p.kind='session' AND p.session_id=e.id AND p.source_machine=e.machine AND p.source_generation=0
          LEFT JOIN engine_run r ON r.id=e.run_id AND r.machine=e.machine
          WHERE e.holder='local' AND e.state<>'foreign'
          UNION ALL
          SELECT e.entity_id, e.machine, e.generation,
            CASE WHEN json_type(e.row_json,'$.parentId')='text' THEN json_extract(e.row_json,'$.parentId') END,
            CASE WHEN json_type(e.row_json,'$.model')='text' THEN json_extract(e.row_json,'$.model') END,
            CASE WHEN json_type(r.row_json,'$.worktree')='text' THEN json_extract(r.row_json,'$.worktree') END,
            CASE WHEN json_type(e.row_json,'$.activityState')='text' THEN json_extract(e.row_json,'$.activityState') END,
            CASE WHEN json_type(e.row_json,'$.branch')='text' THEN json_extract(e.row_json,'$.branch') END
          FROM engine_mirror_row e JOIN page p ON p.kind='session' AND p.session_id=e.entity_id
            AND p.source_machine=e.machine AND p.source_generation=e.generation
          JOIN engine_mirror m ON m.machine=e.machine AND m.generation=e.generation AND m.state='live'
          LEFT JOIN engine_mirror_row r ON r.machine=e.machine AND r.generation=e.generation AND r.entity_kind='run'
            AND r.entity_id=json_extract(e.row_json,'$.runId') AND json_type(r.row_json,'$.id')='text'
            AND json_type(r.row_json,'$.machine')='text' AND json_extract(r.row_json,'$.id')=r.entity_id
            AND json_extract(r.row_json,'$.machine')=r.machine
          WHERE e.entity_kind='session' AND json_type(e.row_json,'$.id')='text' AND json_type(e.row_json,'$.machine')='text'
            AND json_extract(e.row_json,'$.id')=e.entity_id
            AND json_extract(e.row_json,'$.machine')=e.machine AND json_extract(e.row_json,'$.holder')='local'
            AND json_extract(e.row_json,'$.state')<>'foreign'),
        family AS (
          SELECT p.*,
            (SELECT json_extract(t.payload, '$.state') FROM rows t INDEXED BY rows_summary
             WHERE p.kind = 'session' AND t.session_id = p.facade_id AND {SummaryKinds} AND t.kind = 'turn'
             ORDER BY t.item_id DESC, t.seq DESC LIMIT 1) AS turn,
            EXISTS (SELECT 1 FROM rows w INDEXED BY rows_summary
             WHERE p.kind = 'session' AND w.session_id = p.facade_id AND {SummaryKinds} AND w.kind = 'task'
               AND json_extract(w.payload, '$.state') IN ('running', 'requires_action')
               AND NOT EXISTS (SELECT 1 FROM rows l INDEXED BY rows_summary WHERE l.session_id = w.session_id AND {SummaryKinds}
                               AND l.kind = w.kind AND l.item_id = w.item_id AND l.seq > w.seq)) AS busy,
            EXISTS (SELECT 1 FROM rows n INDEXED BY rows_summary
             WHERE p.kind = 'session' AND n.session_id = p.facade_id AND {SummaryKinds} AND n.kind = 'subagent'
               AND n.item_id IS NOT NULL) AS native_activity
          FROM page p)
        SELECT 0, 0, NULL, NULL, NULL, NULL, NULL, NULL, needs, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
          NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL FROM totals
        UNION ALL
        SELECT 1, ROW_NUMBER() OVER (ORDER BY agent), NULL, NULL, NULL, agent, NULL, NULL, NULL, NULL, NULL, NULL,
          NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
        FROM (SELECT DISTINCT agent FROM rail_roster)
        UNION ALL
        SELECT 2, p.n, p.session_id, p.kind, p.section, p.agent, p.repo_url, p.group_label, p.count, p.machines, CAST(p.title AS BLOB),
          p.machine, p.updated_at, p.completed_at, (e.activity_state IS 'running' OR p.turn IS 'started' OR p.busy), p.turn IS 'failed',
          COALESCE((SELECT e.seq FROM rows e INDEXED BY rows_summary WHERE p.kind = 'session' AND e.session_id = p.facade_id
                    AND {SummaryKinds} AND e.kind = 'turn' AND json_extract(e.payload, '$.state') IN ('finished', 'failed', 'cancelled')
                    ORDER BY e.item_id DESC, e.seq DESC LIMIT 1), 0) > p.read_seq,
          e.branch,
          p.section_index, p.header, p.ordering,
          (SELECT NULLIF(substr(trim(replace(group_concat(t.text, ' ' ORDER BY t.n), char(10), ' ')), 1, 160), '') FROM (
             SELECT k.key AS n, (SELECT group_concat(v.atom, '' ORDER BY v.id) FROM json_tree(k.value) v
                                 WHERE v.key = 'value' AND v.type = 'text') AS text
             FROM json_each(m.payload, '$.blocks') k) t),
          p.source_machine, p.source_generation, p.source_route, e.parent_id, s.pinned_at, s.orchestrator_at, s.position,
          e.model, e.worktree,
          (SELECT substr(trim(replace(replace(replace(replace(replace(replace(
             json_extract(err.payload,'$.message'),char(9),' '),char(10),' '),char(13),' '),char(133),' '),char(8232),' '),char(8233),' ')),1,200)
           FROM rows err WHERE p.kind='session' AND err.session_id=p.facade_id AND err.kind='error' ORDER BY err.seq DESC LIMIT 1),
          NULL, NULL, CASE WHEN e.parent_id IS NULL THEN 0 ELSE 1 END, p.native_activity
        FROM family p
        LEFT JOIN rail_roster s ON p.kind='session' AND s.session_id=p.session_id AND s.source_machine IS p.source_machine
          AND s.source_generation IS p.source_generation AND s.source_route IS p.source_route
        LEFT JOIN engine_sessions e ON e.id=p.session_id AND e.machine=p.source_machine AND e.generation=p.source_generation
        LEFT JOIN rows m ON p.kind = 'session' AND m.session_id = p.facade_id AND m.seq = (
          SELECT r.seq FROM rows r WHERE r.session_id = p.facade_id AND r.kind = 'message' ORDER BY r.seq DESC LIMIT 1)
        ORDER BY 1, 2;
        """;

    // The columns of every rail entry, headers and sessions alike.
    private const string RailColumns = """
        session_id, kind, section, agent, repo_url, group_label, count, machines, title, machine, updated_at, completed_at,
        read_seq, section_index, order_group, order_repo, header, name, source_machine, source_generation, source_route, facade_id
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
