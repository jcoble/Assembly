using System.Text.Json;
using Assembly.Engine.Database;
using Assembly.Engine.Markdown;
using Assembly.Engine.Rows;
using Assembly.Engine.Rows.Payloads;

namespace Assembly.Engine.Store;

/// <summary>
/// The rows, in SQLite. One writer. A batch's rows get the thread's next seqs and are committed in one
/// transaction with their tool details, and the saved rows come back to be pushed as they are. A message's
/// deltas are deleted in the transaction that saves its message row, so seqs can have gaps; next_seq never
/// goes back, and each row sent out carries the seq before it.
/// </summary>
public sealed class RowStore
{
    /// <summary>The row format. Two machines copy rows between them only when theirs match.</summary>
    public const int FormatVersion = 3;

    private const string Columns = "thread_id, seq, run_id, item_id, kind, payload, created_at";

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
            scope.Execute(Schema.Create);
            // Version 1 kept every delta: delete the finished messages' deltas it left, once. A newer version's
            // database is left as it is, version included.
            if (scope.QueryFirst("PRAGMA user_version;", row => row.Long(0)) < FormatVersion)
            {
                scope.Execute(
                    """
                    DELETE FROM rows WHERE kind = 'delta' AND EXISTS (SELECT 1 FROM rows m INDEXED BY rows_item
                      WHERE m.thread_id = rows.thread_id AND m.item_id = rows.item_id AND m.kind = 'message');
                    """);
                scope.Execute($"PRAGMA user_version = {FormatVersion};");
            }

            return 0;
        });
        return new RowStore(db, db.Writer);
    }

    public int StoredFormatVersion => (int)_db.QueryFirst("PRAGMA user_version;", row => row.Long(0), CancellationToken.None);

    /// <summary>Saves the batch for the thread and returns its rows as saved, in seq order.</summary>
    public IReadOnlyList<StoredRow> Commit(string threadId, RowBatch batch)
    {
        if (batch.IsEmpty)
            return [];

        return _writer.Write("rows.commit", scope =>
        {
            scope.Execute("INSERT INTO threads(thread_id, next_seq) VALUES ($thread, 1) ON CONFLICT DO NOTHING;", new SqlArg("$thread", threadId));
            var seq = NextSeq(scope, threadId);

            var saved = new List<StoredRow>(batch.Rows.Count);
            foreach (var row in batch.Rows)
            {
                scope.Execute(
                    "INSERT INTO rows(thread_id, seq, run_id, item_id, kind, payload, created_at) VALUES ($thread, $seq, $run, $item, $kind, $payload, $at);",
                    new SqlArg("$thread", threadId), new SqlArg("$seq", seq), new SqlArg("$run", row.RunId), new SqlArg("$item", row.ItemId), new SqlArg("$kind", row.Kind),
                    new SqlArg("$payload", row.Payload.GetRawText()), new SqlArg("$at", row.CreatedAt));
                DeleteDeltasOfFinished(scope, threadId, row.ItemId, row.Kind);
                saved.Add(new StoredRow(threadId, seq, row.RunId, row.ItemId, row.Kind, row.Payload, row.CreatedAt, PrevSeq: seq - 1));
                seq++;
            }

            scope.Execute("UPDATE threads SET next_seq = $seq WHERE thread_id = $thread;", new SqlArg("$thread", threadId), new SqlArg("$seq", seq));
            foreach (var detail in batch.Details)
                UpsertToolDetail(scope, threadId, detail);
            return (IReadOnlyList<StoredRow>)saved;
        });
    }

    /// <summary>
    /// Saves a row another machine committed, under its seq, if its <c>PrevSeq</c> is this thread's last seq.
    /// False for a row already here or one past rows this machine missed; the copier then asks again from
    /// <see cref="LastSeq"/>.
    /// </summary>
    public bool Copy(StoredRow row) =>
        _writer.Write("rows.copy", scope =>
        {
            // A thread with no rows here yet starts at seq 1. A row that doesn't follow on writes nothing.
            var next = Math.Max(NextSeq(scope, row.ThreadId), 1);
            if (row.PrevSeq != next - 1 || row.Seq < next)
                return false;

            scope.Execute("INSERT INTO threads(thread_id, next_seq) VALUES ($thread, 1) ON CONFLICT DO NOTHING;", new SqlArg("$thread", row.ThreadId));

            scope.Execute(
                "INSERT INTO rows(thread_id, seq, run_id, item_id, kind, payload, created_at) VALUES ($thread, $seq, $run, $item, $kind, $payload, $at);",
                new SqlArg("$thread", row.ThreadId), new SqlArg("$seq", row.Seq), new SqlArg("$run", row.RunId), new SqlArg("$item", row.ItemId), new SqlArg("$kind", row.Kind),
                new SqlArg("$payload", row.Payload.GetRawText()), new SqlArg("$at", row.CreatedAt));
            DeleteDeltasOfFinished(scope, row.ThreadId, row.ItemId, row.Kind);
            scope.Execute("UPDATE threads SET next_seq = $seq WHERE thread_id = $thread;", new SqlArg("$thread", row.ThreadId), new SqlArg("$seq", row.Seq + 1));
            return true;
        });

    /// <summary>The thread's newest seq here, or 0 if it has no rows.</summary>
    public long LastSeq(string threadId) =>
        _db.QueryFirst("SELECT next_seq - 1 FROM threads WHERE thread_id = $thread;", row => row.Long(0), CancellationToken.None,
            new SqlArg("$thread", threadId));

    /// <summary>Saves a tool call's detail fetched from the machine that ran it.</summary>
    public void SaveToolDetail(string threadId, ToolDetail detail) =>
        _writer.Write("rows.tool_detail", scope => UpsertToolDetail(scope, threadId, detail));

    /// <summary>
    /// Every row with seq above <paramref name="after"/>, in seq order. This is how a copy catches up. The first
    /// row's <c>PrevSeq</c> is <paramref name="after"/>, since nothing is kept between them.
    /// </summary>
    public IReadOnlyList<StoredRow> RowsAfter(string threadId, long after)
    {
        var prev = after;
        return Query($"SELECT {Columns} FROM rows WHERE thread_id = $thread AND seq > $after ORDER BY seq;", reader =>
        {
            var row = ReadRow(reader) with { PrevSeq = prev };
            prev = row.Seq;
            return row;
        }, new SqlArg("$thread", threadId), new SqlArg("$after", after));
    }

    /// <summary>
    /// The newest <paramref name="limit"/> items that start below <paramref name="before"/>, each in its newest form.
    /// An item's rows never split across pages.
    /// </summary>
    public RowPage PageBefore(string threadId, long before, int limit)
    {
        var picked = 0L;
        var rows = Query(Schema.PageBefore, reader =>
        {
            picked = reader.Long(9);
            var row = ReadRow(reader) with { FirstSeq = reader.Long(7) };
            // A message still streaming comes back as one delta that draws all of its text.
            return reader.IsNull(8) ? row : row with { Payload = WholeDelta(reader.Text(8)) };
        }, new SqlArg("$thread", threadId), new SqlArg("$before", before), new SqlArg("$limit", limit));

        // One item more than asked for says there is more. It's the oldest, and all its rows go.
        var hasBefore = picked > limit;
        if (hasBefore)
            rows = rows.Where(row => row.FirstSeq != rows[0].FirstSeq).ToList();
        return new RowPage(rows, hasBefore, rows.Count > 0 ? rows[0].FirstSeq : null);
    }

    /// <summary>Runs whose newest turn row is still <c>started</c>: the backend stopped while they ran.</summary>
    public IReadOnlyList<(string ThreadId, string RunId)> UnfinishedRuns() =>
        Query(
            """
            SELECT r.thread_id, r.run_id FROM rows r
            WHERE r.kind = 'turn' AND r.run_id IS NOT NULL AND json_extract(r.payload, '$.state') = 'started'
              AND NOT EXISTS (SELECT 1 FROM rows e WHERE e.thread_id = r.thread_id AND e.run_id = r.run_id AND e.kind = 'turn' AND e.seq > r.seq)
            ORDER BY r.thread_id, r.seq;
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
              AND NOT EXISTS (SELECT 1 FROM rows e WHERE e.thread_id = r.thread_id AND e.item_id = r.item_id AND e.kind = $kind AND e.seq > r.seq)
            ORDER BY r.thread_id, r.seq;
            """,
            reader => ReadRow(reader), new SqlArg("$kind", kind));

    /// <summary>The run of the thread's newest turn row if that row says <c>started</c>, else null.</summary>
    public string? OpenRun(string threadId) =>
        Query(
            """
            SELECT run_id, json_extract(payload, '$.state') FROM rows WHERE thread_id = $thread AND kind = 'turn'
            ORDER BY seq DESC LIMIT 1;
            """,
            reader => reader.Text(1) == "started" ? Text(reader, 0) : null, new SqlArg("$thread", threadId)).FirstOrDefault();

    /// <summary>Whether the run has a turn row that ends it.</summary>
    public bool RunEnded(string threadId, string runId) =>
        _db.QueryFirst(
            """
            SELECT EXISTS (SELECT 1 FROM rows WHERE thread_id = $thread AND run_id = $run AND kind = 'turn'
                           AND json_extract(payload, '$.state') <> 'started');
            """,
            row => row.Long(0), CancellationToken.None, new SqlArg("$thread", threadId), new SqlArg("$run", runId)) == 1;

    /// <summary>The thread a run belongs to, or null if no row has it.</summary>
    public string? ThreadOfRun(string runId) =>
        Query("SELECT thread_id FROM rows WHERE run_id = $run LIMIT 1;", reader => reader.Text(0), new SqlArg("$run", runId)).FirstOrDefault();

    /// <summary>Every row of one run, in seq order.</summary>
    public IReadOnlyList<StoredRow> RunRows(string threadId, string runId) =>
        Query($"SELECT {Columns} FROM rows WHERE thread_id = $thread AND run_id = $run ORDER BY seq;",
            reader => ReadRow(reader), new SqlArg("$thread", threadId), new SqlArg("$run", runId));

    public StoredToolCall? ToolCall(string threadId, string itemId) =>
        Query(
            """
            SELECT (SELECT json_extract(payload, '$.summary') FROM rows WHERE thread_id = $thread AND item_id = $item AND kind = 'tool'
                    ORDER BY seq DESC LIMIT 1), input, output, diff, path
            FROM tool_details WHERE thread_id = $thread AND item_id = $item;
            """,
            reader => new StoredToolCall(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4)),
            new SqlArg("$thread", threadId), new SqlArg("$item", itemId)).FirstOrDefault();

    private static long NextSeq(IWriteScope scope, string threadId) =>
        scope.QueryFirst("SELECT next_seq FROM threads WHERE thread_id = $thread;", row => row.Long(0),
            new SqlArg("$thread", threadId));

    /// <summary>A message row is the full form of its deltas, so they go once it's saved.</summary>
    private static void DeleteDeltasOfFinished(IWriteScope scope, string threadId, string? itemId, string kind)
    {
        if (kind == RowKind.Message)
            scope.Execute("DELETE FROM rows WHERE thread_id = $thread AND item_id = $item AND kind = 'delta';", new SqlArg("$thread", threadId), new SqlArg("$item", itemId));
    }

    private static int UpsertToolDetail(IWriteScope scope, string threadId, ToolDetail detail) =>
        scope.Execute(
            """
            INSERT INTO tool_details(thread_id, item_id, input, output, diff, path) VALUES ($thread, $item, $input, $output, $diff, $path)
            ON CONFLICT (thread_id, item_id) DO UPDATE SET
              input = COALESCE(excluded.input, input), output = COALESCE(excluded.output, output),
              diff = COALESCE(excluded.diff, diff), path = COALESCE(excluded.path, path);
            """,
            new SqlArg("$thread", threadId), new SqlArg("$item", detail.ItemId), new SqlArg("$input", detail.Input), new SqlArg("$output", detail.Output),
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
