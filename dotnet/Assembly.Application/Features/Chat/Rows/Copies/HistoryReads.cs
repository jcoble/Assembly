using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Application.Features.Chat.Rows.Copies;

/// <summary>A session's history as its owner holds it: its oldest seq, its newest seq, and its rows that are not deltas.</summary>
public sealed record HistoryFacts(long Low, long Head, long Count);

/// <summary>
/// Whole items, newest first, with the facts read in the same snapshot, and the details of their tool items. No rows
/// means the download is done.
/// </summary>
public sealed record BeforeChunk(IReadOnlyList<JsonElement> Rows, HistoryFacts Facts, IReadOnlyList<SavedToolDetail>? Details = null);

/// <summary>A tool item of a run, at its last row's seq.</summary>
public sealed record RunTool(string Item, long Seq);

/// <summary>The owner's reads a saved copy downloads from (TSK-1692).</summary>
public sealed class HistoryReads(IAppDatabase db)
{
    /// <summary>A chunk stops before its row JSON passes this. Its first item always comes.</summary>
    public const long ChunkBudget = 1024 * 1024;

    // The most items a chunk weighs: a 1 MB chunk of the smallest rows holds fewer, so no chunk sizes a whole history.
    private const int ChunkItems = 5000;

    // Rough per-row JSON around its payload: the field names, ids and numbers.
    private const int RowOverhead = 160;

    private const string FactsSql = """
        SELECT s.session_id, COALESCE((SELECT MIN(seq) FROM rows r WHERE r.session_id=s.session_id), 0), s.next_seq-1,
          (SELECT COUNT(*) FROM rows r WHERE r.session_id=s.session_id AND r.kind<>'delta' AND r.seq<s.next_seq)
        FROM sessions s WHERE s.session_id IN (SELECT value FROM json_each($sessions))
        """;

    // An item is its item id's rows, or one row without an item id. It sits at its first row's seq.
    private const string ItemsBeforeSql = """
        WITH items AS (
          SELECT r.seq AS first_seq, r.item_id FROM rows r
          WHERE r.session_id=$session AND r.seq<$before
            AND (r.item_id IS NULL OR NOT EXISTS (SELECT 1 FROM rows f INDEXED BY rows_item
                 WHERE f.session_id=r.session_id AND f.item_id=r.item_id AND f.seq<r.seq))
          ORDER BY r.seq DESC LIMIT $items),
        member AS (
          SELECT i.first_seq, r.rowid AS row_key, length(CAST(r.payload AS BLOB))+$overhead AS bytes
          FROM items i JOIN rows r INDEXED BY rows_item ON r.session_id=$session AND r.item_id=i.item_id
          WHERE i.item_id IS NOT NULL
          UNION ALL
          SELECT i.first_seq, r.rowid, length(CAST(r.payload AS BLOB))+$overhead
          FROM items i JOIN rows r ON r.session_id=$session AND r.seq=i.first_seq
          WHERE i.item_id IS NULL),
        sized AS (SELECT first_seq, SUM(bytes) AS bytes FROM member GROUP BY first_seq),
        running AS (
          SELECT first_seq, SUM(bytes) OVER (ORDER BY first_seq DESC ROWS UNBOUNDED PRECEDING) AS total,
            ROW_NUMBER() OVER (ORDER BY first_seq DESC) AS n FROM sized),
        kept AS (SELECT first_seq FROM running WHERE n=1 OR total<=$budget)
        SELECT r.session_id, r.seq, r.run_id, r.item_id, r.kind, r.payload, r.created_at
        FROM kept k JOIN member m ON m.first_seq=k.first_seq JOIN rows r ON r.rowid=m.row_key
        ORDER BY k.first_seq DESC, r.seq
        """;

    /// <summary>
    /// Whole items whose first row is below <paramref name="before"/> (from the top when null), newest first with each
    /// item's rows in seq order, under <see cref="ChunkBudget"/>; at most <paramref name="items"/> of them when given.
    /// The rows and the facts come from one snapshot.
    /// </summary>
    public BeforeChunk ItemsBefore(string session, long? before, int? items, CancellationToken ct = default)
    {
        using var snapshot = db.OpenSnapshot(ct);
        var facts = ReadFacts(snapshot, [session]).GetValueOrDefault(session, new HistoryFacts(0, 0, 0));
        var rows = snapshot.Query(ItemsBeforeSql, row => RowWire.ToElement(RowStore.ReadRow(row)),
            new SqlArg("$session", session), new SqlArg("$before", before ?? long.MaxValue), new SqlArg("$items", Math.Min(items ?? ChunkItems, ChunkItems)),
            new SqlArg("$overhead", RowOverhead), new SqlArg("$budget", ChunkBudget));
        return new BeforeChunk(rows, facts);
    }

    /// <summary>The run's user rows, oldest first.</summary>
    public IReadOnlyList<JsonElement> UserRows(string session, string run, CancellationToken ct = default) => db.Query(
        "SELECT session_id,seq,run_id,item_id,kind,payload,created_at FROM rows WHERE session_id=$session AND run_id=$run AND kind='user' ORDER BY seq",
        row => RowWire.ToElement(RowStore.ReadRow(row)), ct, new SqlArg("$session", session), new SqlArg("$run", run));

    /// <summary>The run's tool items whose last row is past <paramref name="after"/>, in that row's order.</summary>
    public IReadOnlyList<RunTool> ToolItems(string session, string run, long after, CancellationToken ct = default) => db.Query("""
        SELECT item_id, MAX(seq) AS last FROM rows
        WHERE session_id=$session AND run_id=$run AND kind='tool' AND item_id IS NOT NULL
        GROUP BY item_id HAVING last>$after ORDER BY last
        """, row => new RunTool(row.Text(0), row.Long(1)), ct, new SqlArg("$session", session), new SqlArg("$run", run),
        new SqlArg("$after", after));

    /// <summary>Each named session's facts, in one query. A session with no rows here is left out.</summary>
    public IReadOnlyDictionary<string, HistoryFacts> Facts(IReadOnlyList<string> sessions, CancellationToken ct = default)
    {
        using var snapshot = db.OpenSnapshot(ct);
        return ReadFacts(snapshot, sessions);
    }

    private static Dictionary<string, HistoryFacts> ReadFacts(IReadScope scope, IReadOnlyList<string> sessions) =>
        scope.Query(FactsSql, row => (row.Text(0), new HistoryFacts(row.Long(1), row.Long(2), row.Long(3))),
                new SqlArg("$sessions", JsonSerializer.Serialize(sessions, RowJsonContext.Rows.IReadOnlyListString)))
            .ToDictionary(pair => pair.Item1, pair => pair.Item2);
}
