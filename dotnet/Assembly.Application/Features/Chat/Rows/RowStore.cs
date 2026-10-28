using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows.Markdown;
using Assembly.Domain.Markdown;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// The rows, in SQLite. One writer. A batch's rows get the session's next seqs and are committed in one
/// transaction with their tool details, and the saved rows come back to be pushed as they are. A message's
/// deltas are deleted in the transaction that saves its message row, so seqs can have gaps; next_seq never
/// goes back, and each row sent out carries the seq before it.
/// </summary>
public sealed class RowStore
{
    /// <summary>The row format. Two machines copy rows between them only when theirs match. 4: the session names.</summary>
    public const int FormatVersion = 4;

    private const string Columns = "session_id, seq, run_id, item_id, kind, payload, created_at";

    private readonly IAppDatabase _db;
    private readonly IAtomicWriter _writer;

    private RowStore(IAppDatabase db, IAtomicWriter writer)
    {
        _db = db;
        _writer = writer;
    }

    public static RowStore Open(IAppDatabase db)
    {
        db.Writer.Write("rows.schema", scope =>
        {
            Schema.RenameOldNames(scope);
            scope.Execute(Schema.Create);
            // Version 1 kept every delta: delete the finished messages' deltas it left, once. A newer version's
            // database is left as it is, version included.
            if (scope.QueryFirst("PRAGMA user_version;", row => row.Long(0)) < FormatVersion)
            {
                scope.Execute(
                    """
                    DELETE FROM rows WHERE kind = 'delta' AND EXISTS (SELECT 1 FROM rows m INDEXED BY rows_item
                      WHERE m.session_id = rows.session_id AND m.item_id = rows.item_id AND m.kind = 'message');
                    """);
                scope.Execute($"PRAGMA user_version = {FormatVersion};");
            }

            return 0;
        });
        return new RowStore(db, db.Writer);
    }

    public int StoredFormatVersion => (int)_db.QueryFirst("PRAGMA user_version;", row => row.Long(0), CancellationToken.None);

    /// <summary>Saves the batch for the session and returns its rows as saved, in seq order.</summary>
    public IReadOnlyList<StoredRow> Commit(string sessionId, RowBatch batch)
    {
        if (batch.IsEmpty)
            return [];

        return _writer.Write("rows.commit", scope =>
        {
            scope.Execute("INSERT INTO sessions(session_id, next_seq) VALUES ($session, 1) ON CONFLICT DO NOTHING;", new SqlArg("$session", sessionId));
            var seq = NextSeq(scope, sessionId);

            var saved = new List<StoredRow>(batch.Rows.Count);
            foreach (var row in batch.Rows)
            {
                scope.Execute(
                    "INSERT INTO rows(session_id, seq, run_id, item_id, kind, payload, created_at) VALUES ($session, $seq, $run, $item, $kind, $payload, $at);",
                    new SqlArg("$session", sessionId), new SqlArg("$seq", seq), new SqlArg("$run", row.RunId), new SqlArg("$item", row.ItemId), new SqlArg("$kind", row.Kind),
                    new SqlArg("$payload", row.Payload.GetRawText()), new SqlArg("$at", row.CreatedAt));
                DeleteDeltasOfFinished(scope, sessionId, row.ItemId, row.Kind);
                saved.Add(new StoredRow(sessionId, seq, row.RunId, row.ItemId, row.Kind, row.Payload, row.CreatedAt, PrevSeq: seq - 1));
                seq++;
            }

            scope.Execute("UPDATE sessions SET next_seq = $seq WHERE session_id = $session;", new SqlArg("$session", sessionId), new SqlArg("$seq", seq));
            foreach (var detail in batch.Details)
                UpsertToolDetail(scope, sessionId, detail);
            return (IReadOnlyList<StoredRow>)saved;
        });
    }

    /// <summary>
    /// Saves a row another machine committed, under its seq, if its <c>PrevSeq</c> is this session's last seq.
    /// False for a row already here or one past rows this machine missed; the copier then asks again from
    /// <see cref="LastSeq"/>.
    /// </summary>
    public bool Copy(StoredRow row) =>
        _writer.Write("rows.copy", scope =>
        {
            // A session with no rows here yet starts at seq 1. A row that doesn't follow on writes nothing.
            var next = Math.Max(NextSeq(scope, row.SessionId), 1);
            if (row.PrevSeq != next - 1 || row.Seq < next)
                return false;

            scope.Execute("INSERT INTO sessions(session_id, next_seq) VALUES ($session, 1) ON CONFLICT DO NOTHING;", new SqlArg("$session", row.SessionId));

            scope.Execute(
                "INSERT INTO rows(session_id, seq, run_id, item_id, kind, payload, created_at) VALUES ($session, $seq, $run, $item, $kind, $payload, $at);",
                new SqlArg("$session", row.SessionId), new SqlArg("$seq", row.Seq), new SqlArg("$run", row.RunId), new SqlArg("$item", row.ItemId), new SqlArg("$kind", row.Kind),
                new SqlArg("$payload", row.Payload.GetRawText()), new SqlArg("$at", row.CreatedAt));
            DeleteDeltasOfFinished(scope, row.SessionId, row.ItemId, row.Kind);
            scope.Execute("UPDATE sessions SET next_seq = $seq WHERE session_id = $session;", new SqlArg("$session", row.SessionId), new SqlArg("$seq", row.Seq + 1));
            return true;
        });

    /// <summary>The session's newest seq here, or 0 if it has no rows.</summary>
    public long LastSeq(string sessionId) =>
        _db.QueryFirst("SELECT next_seq - 1 FROM sessions WHERE session_id = $session;", row => row.Long(0), CancellationToken.None,
            new SqlArg("$session", sessionId));

    /// <summary>Saves a tool call's detail fetched from the machine that ran it.</summary>
    public void SaveToolDetail(string sessionId, ToolDetail detail) =>
        _writer.Write("rows.tool_detail", scope => UpsertToolDetail(scope, sessionId, detail));

    /// <summary>
    /// Every row with seq above <paramref name="after"/>, in seq order. This is how a copy catches up. The first
    /// row's <c>PrevSeq</c> is <paramref name="after"/>, since nothing is kept between them. At most
    /// <paramref name="limit"/> of them, or all with -1.
    /// </summary>
    public IReadOnlyList<StoredRow> RowsAfter(string sessionId, long after, int limit = -1)
    {
        var prev = after;
        return Query($"SELECT {Columns} FROM rows WHERE session_id = $session AND seq > $after ORDER BY seq LIMIT $limit;", reader =>
        {
            var row = ReadRow(reader) with { PrevSeq = prev };
            prev = row.Seq;
            return row;
        }, new SqlArg("$session", sessionId), new SqlArg("$after", after), new SqlArg("$limit", limit));
    }

    /// <summary>
    /// The newest <paramref name="limit"/> items that start below <paramref name="before"/>, each in its newest form.
    /// An item's rows never split across pages.
    /// </summary>
    public RowPage PageBefore(string sessionId, long before, int limit, CancellationToken cancel = default)
    {
        var picked = 0L;
        var rows = _db.Query(Schema.PageBefore, reader =>
        {
            picked = reader.Long(9);
            var row = ReadRow(reader) with { FirstSeq = reader.Long(7) };
            // A message still streaming comes back as one delta that draws all of its text.
            return reader.IsNull(8) ? row : row with { Payload = WholeDelta(reader.Text(8)) };
        }, cancel, new SqlArg("$session", sessionId), new SqlArg("$before", before), new SqlArg("$limit", limit));

        // One item more than asked for says there is more. It's the oldest, and all its rows go.
        var hasBefore = picked > limit;
        if (hasBefore)
            rows = rows.Where(row => row.FirstSeq != rows[0].FirstSeq).ToList();
        return new RowPage(rows, hasBefore, rows.Count > 0 ? rows[0].FirstSeq : null);
    }

    /// <summary>Runs whose newest turn row is still <c>started</c>: the backend stopped while they ran.</summary>
    public IReadOnlyList<(string SessionId, string RunId)> UnfinishedRuns() =>
        Query(
            """
            SELECT r.session_id, r.run_id FROM rows r
            WHERE r.kind = 'turn' AND r.run_id IS NOT NULL AND json_extract(r.payload, '$.state') = 'started'
              AND NOT EXISTS (SELECT 1 FROM rows e WHERE e.session_id = r.session_id AND e.run_id = r.run_id AND e.kind = 'turn' AND e.seq > r.seq)
            ORDER BY r.session_id, r.seq;
            """,
            reader => (reader.Text(0), reader.Text(1)));

    /// <summary>
    /// Background tasks or subagents (by <paramref name="kind"/>) whose newest row says they're at work: the backend
    /// stopped while they ran.
    /// </summary>
    public IReadOnlyList<StoredRow> RunningItems(string kind) =>
        Query(
            $"""
            SELECT {Columns} FROM rows r
            WHERE r.kind = $kind AND json_extract(r.payload, '$.state') IN ('running', 'requires_action')
              AND NOT EXISTS (SELECT 1 FROM rows e WHERE e.session_id = r.session_id AND e.item_id = r.item_id AND e.kind = $kind AND e.seq > r.seq)
            ORDER BY r.session_id, r.seq;
            """,
            reader => ReadRow(reader), new SqlArg("$kind", kind));

    /// <summary>
    /// For <see cref="SessionSummaries"/>, every session's newest turn row and the newest row of each of its asks, tasks
    /// and subagents, in session and seq order.
    /// </summary>
    public IReadOnlyList<StoredRow> SummaryRows() => Query(Schema.SummaryRows, reader => ReadRow(reader));

    /// <summary>For <see cref="SessionSummaries"/>, the seq of the session's newest turn row that ended a turn, or 0.</summary>
    public long LastTurnEnd(string sessionId) =>
        Query(Schema.LastTurnEnd, reader => reader.Long(0), new SqlArg("$session", sessionId)).FirstOrDefault();

    /// <summary>The run of the session's newest turn row if that row says <c>started</c>, else null.</summary>
    public string? OpenRun(string sessionId) =>
        Query(
            """
            SELECT run_id, json_extract(payload, '$.state') FROM rows WHERE session_id = $session AND kind = 'turn'
            ORDER BY seq DESC LIMIT 1;
            """,
            reader => reader.Text(1) == "started" ? Text(reader, 0) : null, new SqlArg("$session", sessionId)).FirstOrDefault();

    /// <summary>The run and text of the session's newest user row sent with <paramref name="clientId"/>, or null.</summary>
    public (string? RunId, string Text)? UserRowOfClient(string sessionId, string clientId) =>
        Query(Schema.UserRowOfClient, reader => ((string? RunId, string Text)?)(Text(reader, 0), reader.Text(1)),
            new SqlArg("$session", sessionId), new SqlArg("$client", clientId)).FirstOrDefault();

    /// <summary>Whether the run has a turn row that ends it.</summary>
    public bool RunEnded(string sessionId, string runId) =>
        _db.QueryFirst(
            """
            SELECT EXISTS (SELECT 1 FROM rows WHERE session_id = $session AND run_id = $run AND kind = 'turn'
                           AND json_extract(payload, '$.state') <> 'started');
            """,
            row => row.Long(0), CancellationToken.None, new SqlArg("$session", sessionId), new SqlArg("$run", runId)) == 1;

    /// <summary>The session a run belongs to, or null if no row has it.</summary>
    public string? SessionOfRun(string runId) =>
        Query("SELECT session_id FROM rows WHERE run_id = $run LIMIT 1;", reader => reader.Text(0), new SqlArg("$run", runId)).FirstOrDefault();

    /// <summary>Every row of one run, in seq order.</summary>
    public IReadOnlyList<StoredRow> RunRows(string sessionId, string runId) =>
        Query($"SELECT {Columns} FROM rows WHERE session_id = $session AND run_id = $run ORDER BY seq;",
            reader => ReadRow(reader), new SqlArg("$session", sessionId), new SqlArg("$run", runId));

    public StoredToolCall? ToolCall(string sessionId, string itemId) =>
        Query(
            """
            SELECT (SELECT json_extract(payload, '$.summary') FROM rows WHERE session_id = $session AND item_id = $item AND kind = 'tool'
                    ORDER BY seq DESC LIMIT 1), input, output, diff, path
            FROM tool_details WHERE session_id = $session AND item_id = $item;
            """,
            reader => new StoredToolCall(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4)),
            new SqlArg("$session", sessionId), new SqlArg("$item", itemId)).FirstOrDefault();

    /// <summary>
    /// The files the session's tool calls edited, deleted or moved, newest first (<c>rows/files</c>). Each call counts once,
    /// by its newest row, since a call writes one as it starts and another as it ends; a failed call touched nothing. A call
    /// that changed several files lists each in <c>files</c>; one that changed one names it in <c>path</c>.
    /// </summary>
    public IReadOnlyList<FileTouched> FilesTouched(string sessionId, CancellationToken cancellationToken) =>
        _db.Query(
            """
            WITH calls AS (
              SELECT r.seq, r.payload FROM rows r
              WHERE r.session_id = $session AND r.kind = 'tool'
                AND json_extract(r.payload, '$.kind') IN ('edit', 'delete', 'move') AND json_extract(r.payload, '$.state') <> 'failed'
                AND NOT EXISTS (SELECT 1 FROM rows l WHERE l.session_id = r.session_id AND l.item_id = r.item_id AND l.kind = 'tool'
                                AND l.seq > r.seq)),
            changes AS (
              SELECT c.seq, json_extract(f.value, '$.path') AS path, json_extract(f.value, '$.added') AS added,
                json_extract(f.value, '$.removed') AS removed
              FROM calls c, json_each(c.payload, '$.files') f
              UNION ALL
              SELECT c.seq, json_extract(c.payload, '$.path'), json_extract(c.payload, '$.added'), json_extract(c.payload, '$.removed')
              FROM calls c WHERE json_extract(c.payload, '$.files') IS NULL)
            SELECT path, SUM(COALESCE(added, 0)), SUM(COALESCE(removed, 0)), MAX(seq) FROM changes
            WHERE path IS NOT NULL
            GROUP BY path ORDER BY 4 DESC, 1;
            """,
            reader => new FileTouched(reader.Text(0), reader.Long(1), reader.Long(2), reader.Long(3)), cancellationToken,
            new SqlArg("$session", sessionId));

    /// <summary>
    /// The last <paramref name="tail"/> characters of the session's newest <paramref name="limit"/> prompts that may have
    /// attached images, newest first (<c>rows/attachments</c>). The composer puts an "Attached image:" line for each after
    /// the text, and they're the only record of the names; the window keeps only the trailing ones, so a prompt that just
    /// mentions the words lists nothing.
    /// </summary>
    public IReadOnlyList<string> Attachments(string sessionId, int limit, int tail, CancellationToken cancellationToken) =>
        _db.Query(
            """
            SELECT substr(text, -$tail)
            FROM (SELECT seq, json_extract(payload, '$.text') AS text FROM rows WHERE session_id = $session AND kind = 'user')
            WHERE instr(text, 'Attached image: ') > 0
            ORDER BY seq DESC
            LIMIT $limit;
            """,
            reader => reader.Text(0), cancellationToken, new SqlArg("$session", sessionId), new SqlArg("$limit", limit),
            new SqlArg("$tail", tail));

    private static long NextSeq(IWriteScope scope, string sessionId) =>
        scope.QueryFirst("SELECT next_seq FROM sessions WHERE session_id = $session;", row => row.Long(0),
            new SqlArg("$session", sessionId));

    /// <summary>A message row is the full form of its deltas, so they go once it's saved.</summary>
    private static void DeleteDeltasOfFinished(IWriteScope scope, string sessionId, string? itemId, string kind)
    {
        if (kind == RowKind.Message)
            scope.Execute("DELETE FROM rows WHERE session_id = $session AND item_id = $item AND kind = 'delta';", new SqlArg("$session", sessionId), new SqlArg("$item", itemId));
    }

    private static int UpsertToolDetail(IWriteScope scope, string sessionId, ToolDetail detail) =>
        scope.Execute(
            """
            INSERT INTO tool_details(session_id, item_id, input, output, diff, path) VALUES ($session, $item, $input, $output, $diff, $path)
            ON CONFLICT (session_id, item_id) DO UPDATE SET
              input = COALESCE(excluded.input, input), output = COALESCE(excluded.output, output),
              diff = COALESCE(excluded.diff, diff), path = COALESCE(excluded.path, path);
            """,
            new SqlArg("$session", sessionId), new SqlArg("$item", detail.ItemId), new SqlArg("$input", detail.Input), new SqlArg("$output", detail.Output),
            new SqlArg("$diff", detail.Diff), new SqlArg("$path", detail.Path));

    private static JsonElement WholeDelta(string text) =>
        JsonSerializer.SerializeToElement(new DeltaPayload(0, SafeMarkdown.Parse(text), text), RowJsonContext.Rows.DeltaPayload);

    private static StoredRow ReadRow(IRow reader)
    {
        using var payload = JsonDocument.Parse(reader.Text(5));
        return new StoredRow(reader.Text(0), reader.Long(1), Text(reader, 2), Text(reader, 3), reader.Text(4),
            payload.RootElement.Clone(), reader.Long(6));
    }

    private static string? Text(IRow reader, int column) => reader.IsNull(column) ? null : reader.Text(column);

    private List<T> Query<T>(string sql, Func<IRow, T> read, params SqlArg[] args) => _db.Query(sql, read, CancellationToken.None, args);
}
