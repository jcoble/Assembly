using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Application.Features.Chat;

/// <summary>The row store's SQL. Kept apart so the queries can be read on their own.</summary>
internal static class Schema
{
    public const string Create = """
        CREATE TABLE IF NOT EXISTS threads(thread_id TEXT PRIMARY KEY, next_seq INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS rows(thread_id TEXT NOT NULL, seq INTEGER NOT NULL, run_id TEXT, item_id TEXT,
          kind TEXT NOT NULL, payload TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (thread_id, seq));
        CREATE INDEX IF NOT EXISTS rows_item ON rows(thread_id, item_id, kind, seq);
        CREATE TABLE IF NOT EXISTS tool_details(thread_id TEXT NOT NULL, item_id TEXT NOT NULL, input TEXT, output TEXT,
          diff TEXT, path TEXT, PRIMARY KEY (thread_id, item_id));
        """;

    /// <summary>
    /// One page, in one statement. <c>shown</c> is each item's newest row of each kind, leaving out a message's
    /// deltas once it has its final row; <c>first_seq</c> is where the item sits. <c>picked</c> is the newest
    /// <c>$limit + 1</c> items below <c>$before</c>. For a delta, column 8 is all its item's delta text, so the
    /// page can draw a message that's still streaming. Column 9 is how many items were picked.
    /// </summary>
    public const string PageBefore = """
        WITH shown AS MATERIALIZED (
          SELECT r.thread_id, r.seq, r.run_id, r.item_id, r.kind, r.payload, r.created_at,
            COALESCE((SELECT MIN(f.seq) FROM rows f INDEXED BY rows_item
                      WHERE f.thread_id = r.thread_id AND f.item_id = r.item_id), r.seq) AS first_seq
          FROM rows r
          WHERE r.thread_id = $thread
            AND NOT (r.kind = 'delta' AND EXISTS (SELECT 1 FROM rows m WHERE m.thread_id = r.thread_id
                     AND m.item_id = r.item_id AND m.kind = 'message'))
            AND NOT EXISTS (SELECT 1 FROM rows l WHERE l.thread_id = r.thread_id AND l.item_id = r.item_id
                            AND l.kind = r.kind AND l.seq > r.seq)),
        picked AS (SELECT DISTINCT first_seq FROM shown WHERE first_seq < $before ORDER BY first_seq DESC LIMIT $limit + 1)
        SELECT p.thread_id, p.seq, p.run_id, p.item_id, p.kind, p.payload, p.created_at, p.first_seq,
          CASE WHEN p.kind = 'delta' THEN (SELECT group_concat(json_extract(d.payload, '$.text'), '' ORDER BY d.seq)
                                           FROM rows d WHERE d.thread_id = p.thread_id AND d.item_id = p.item_id AND d.kind = 'delta') END,
          (SELECT COUNT(*) FROM picked)
        FROM shown p
        WHERE p.first_seq IN picked
        ORDER BY p.first_seq, p.seq;
        """;
}
