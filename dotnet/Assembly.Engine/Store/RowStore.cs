using System.Text.Json;
using Microsoft.Data.Sqlite;
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
public sealed class RowStore : IDisposable
{
    /// <summary>The row format. Two machines copy rows between them only when theirs match.</summary>
    public const int FormatVersion = 3;

    private const string Columns = "thread_id, seq, run_id, item_id, kind, payload, created_at";

    private readonly SqliteConnection _db;

    private RowStore(SqliteConnection db) => _db = db;

    public static RowStore Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        var store = new RowStore(db);
        store.Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
        store.Execute(Schema.Create);
        // Version 1 kept every delta: delete the finished messages' deltas it left, once. A newer version's
        // database is left as it is, version included.
        if (store.StoredFormatVersion < FormatVersion)
        {
            store.Execute(
                """
                DELETE FROM rows WHERE kind = 'delta' AND EXISTS (SELECT 1 FROM rows m INDEXED BY rows_item
                  WHERE m.thread_id = rows.thread_id AND m.item_id = rows.item_id AND m.kind = 'message');
                """);
            store.Execute($"PRAGMA user_version = {FormatVersion};");
        }

        return store;
    }

    public int StoredFormatVersion => Convert.ToInt32(Scalar("PRAGMA user_version;"), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Saves the batch for the thread and returns its rows as saved, in seq order.</summary>
    public IReadOnlyList<StoredRow> Commit(string threadId, RowBatch batch)
    {
        if (batch.IsEmpty)
            return [];

        using var transaction = _db.BeginTransaction();
        Execute("INSERT INTO threads(thread_id, next_seq) VALUES ($thread, 1) ON CONFLICT DO NOTHING;", ("$thread", threadId));
        var seq = (long)Scalar("SELECT next_seq FROM threads WHERE thread_id = $thread;", ("$thread", threadId))!;

        var saved = new List<StoredRow>(batch.Rows.Count);
        foreach (var row in batch.Rows)
        {
            Execute(
                "INSERT INTO rows(thread_id, seq, run_id, item_id, kind, payload, created_at) VALUES ($thread, $seq, $run, $item, $kind, $payload, $at);",
                ("$thread", threadId), ("$seq", seq), ("$run", row.RunId), ("$item", row.ItemId), ("$kind", row.Kind),
                ("$payload", row.Payload.GetRawText()), ("$at", row.CreatedAt));
            DeleteDeltasOfFinished(threadId, row.ItemId, row.Kind);
            saved.Add(new StoredRow(threadId, seq, row.RunId, row.ItemId, row.Kind, row.Payload, row.CreatedAt, PrevSeq: seq - 1));
            seq++;
        }

        Execute("UPDATE threads SET next_seq = $seq WHERE thread_id = $thread;", ("$thread", threadId), ("$seq", seq));
        foreach (var detail in batch.Details)
            UpsertToolDetail(threadId, detail);

        transaction.Commit();
        return saved;
    }

    /// <summary>
    /// Saves a row another machine committed, under its seq, if its <c>PrevSeq</c> is this thread's last seq.
    /// False for a row already here or one past rows this machine missed; the copier then asks again from
    /// <see cref="LastSeq"/>.
    /// </summary>
    public bool Copy(StoredRow row)
    {
        using var transaction = _db.BeginTransaction();
        Execute("INSERT INTO threads(thread_id, next_seq) VALUES ($thread, 1) ON CONFLICT DO NOTHING;", ("$thread", row.ThreadId));
        var next = (long)Scalar("SELECT next_seq FROM threads WHERE thread_id = $thread;", ("$thread", row.ThreadId))!;
        if (row.PrevSeq != next - 1 || row.Seq < next)
            return false;

        Execute(
            "INSERT INTO rows(thread_id, seq, run_id, item_id, kind, payload, created_at) VALUES ($thread, $seq, $run, $item, $kind, $payload, $at);",
            ("$thread", row.ThreadId), ("$seq", row.Seq), ("$run", row.RunId), ("$item", row.ItemId), ("$kind", row.Kind),
            ("$payload", row.Payload.GetRawText()), ("$at", row.CreatedAt));
        DeleteDeltasOfFinished(row.ThreadId, row.ItemId, row.Kind);
        Execute("UPDATE threads SET next_seq = $seq WHERE thread_id = $thread;", ("$thread", row.ThreadId), ("$seq", row.Seq + 1));
        transaction.Commit();
        return true;
    }

    /// <summary>The thread's newest seq here, or 0 if it has no rows.</summary>
    public long LastSeq(string threadId) =>
        Scalar("SELECT next_seq - 1 FROM threads WHERE thread_id = $thread;", ("$thread", threadId)) is long seq ? seq : 0;

    /// <summary>Saves a tool call's detail fetched from the machine that ran it.</summary>
    public void SaveToolDetail(string threadId, ToolDetail detail) => UpsertToolDetail(threadId, detail);

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
        }, ("$thread", threadId), ("$after", after));
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
            picked = reader.GetInt64(9);
            var row = ReadRow(reader) with { FirstSeq = reader.GetInt64(7) };
            // A message still streaming comes back as one delta that draws all of its text.
            return reader.IsDBNull(8) ? row : row with { Payload = WholeDelta(reader.GetString(8)) };
        }, ("$thread", threadId), ("$before", before), ("$limit", limit));

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
            reader => (reader.GetString(0), reader.GetString(1)));

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
            reader => ReadRow(reader), ("$kind", kind));

    /// <summary>The run of the thread's newest turn row if that row says <c>started</c>, else null.</summary>
    public string? OpenRun(string threadId) =>
        Query(
            """
            SELECT run_id, json_extract(payload, '$.state') FROM rows WHERE thread_id = $thread AND kind = 'turn'
            ORDER BY seq DESC LIMIT 1;
            """,
            reader => reader.GetString(1) == "started" ? Text(reader, 0) : null, ("$thread", threadId)).FirstOrDefault();

    /// <summary>Whether the run has a turn row that ends it.</summary>
    public bool RunEnded(string threadId, string runId) =>
        Scalar(
            """
            SELECT EXISTS (SELECT 1 FROM rows WHERE thread_id = $thread AND run_id = $run AND kind = 'turn'
                           AND json_extract(payload, '$.state') <> 'started');
            """,
            ("$thread", threadId), ("$run", runId)) is long ended && ended == 1;

    /// <summary>The thread a run belongs to, or null if no row has it.</summary>
    public string? ThreadOfRun(string runId) =>
        Query("SELECT thread_id FROM rows WHERE run_id = $run LIMIT 1;", reader => reader.GetString(0), ("$run", runId)).FirstOrDefault();

    /// <summary>Every row of one run, in seq order.</summary>
    public IReadOnlyList<StoredRow> RunRows(string threadId, string runId) =>
        Query($"SELECT {Columns} FROM rows WHERE thread_id = $thread AND run_id = $run ORDER BY seq;",
            reader => ReadRow(reader), ("$thread", threadId), ("$run", runId));

    public StoredToolCall? ToolCall(string threadId, string itemId) =>
        Query(
            """
            SELECT (SELECT json_extract(payload, '$.summary') FROM rows WHERE thread_id = $thread AND item_id = $item AND kind = 'tool'
                    ORDER BY seq DESC LIMIT 1), input, output, diff, path
            FROM tool_details WHERE thread_id = $thread AND item_id = $item;
            """,
            reader => new StoredToolCall(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4)),
            ("$thread", threadId), ("$item", itemId)).FirstOrDefault();

    public void Dispose() => _db.Dispose();

    /// <summary>A message row is the full form of its deltas, so they go once it's saved.</summary>
    private void DeleteDeltasOfFinished(string threadId, string? itemId, string kind)
    {
        if (kind == RowKind.Message)
            Execute("DELETE FROM rows WHERE thread_id = $thread AND item_id = $item AND kind = 'delta';", ("$thread", threadId), ("$item", itemId));
    }

    private void UpsertToolDetail(string threadId, ToolDetail detail) =>
        Execute(
            """
            INSERT INTO tool_details(thread_id, item_id, input, output, diff, path) VALUES ($thread, $item, $input, $output, $diff, $path)
            ON CONFLICT (thread_id, item_id) DO UPDATE SET
              input = COALESCE(excluded.input, input), output = COALESCE(excluded.output, output),
              diff = COALESCE(excluded.diff, diff), path = COALESCE(excluded.path, path);
            """,
            ("$thread", threadId), ("$item", detail.ItemId), ("$input", detail.Input), ("$output", detail.Output),
            ("$diff", detail.Diff), ("$path", detail.Path));

    private static JsonElement WholeDelta(string text) =>
        JsonSerializer.SerializeToElement(new DeltaPayload(0, SafeMarkdown.Parse(text), text), RowJsonContext.Rows.DeltaPayload);

    private static StoredRow ReadRow(SqliteDataReader reader)
    {
        using var payload = JsonDocument.Parse(reader.GetString(5));
        return new StoredRow(reader.GetString(0), reader.GetInt64(1), Text(reader, 2), Text(reader, 3), reader.GetString(4),
            payload.RootElement.Clone(), reader.GetInt64(6));
    }

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(read(reader));
        return results;
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
