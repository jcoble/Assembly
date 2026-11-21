using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows.Copies;

/// <summary>What saving a download's first chunk did: whether the copy is already whole, and the subagent cards it saved.</summary>
public sealed record CopyStart(bool Complete, IReadOnlyList<StoredRow> SubagentRows);

/// <summary>
/// A download's saves (TSK-1692), each one write. Only <see cref="LiveHub"/> calls them, under its lock, so a live row
/// copied meanwhile is saved once: a row already here is skipped, and the session's next seq never goes back.
/// </summary>
public sealed partial class CopyRecords
{
    /// <summary>
    /// The first chunk, the newest items: its rows, the session's next seq past the remote's head, and the copy record,
    /// complete when the remote has no rows or the chunk reached its oldest.
    /// </summary>
    internal CopyStart Start(string session, string machine, IReadOnlyList<StoredRow> rows, HistoryFacts facts) =>
        writer.Write("rows.copy-start", scope =>
        {
            var saved = Save(scope, session, rows);
            scope.Execute("""
                INSERT INTO sessions(session_id,next_seq) VALUES($session,$next)
                ON CONFLICT(session_id) DO UPDATE SET next_seq=MAX(next_seq, excluded.next_seq)
                """, new SqlArg("$session", session), new SqlArg("$next", facts.Head + 1));
            var complete = facts.Head == 0 || rows.Count > 0 && rows.Min(row => row.Seq) <= facts.Low;
            scope.Execute("""
                INSERT INTO remote_copy(session_id,machine,state,head,count) VALUES($session,$machine,$state,$head,$count)
                ON CONFLICT(session_id) DO UPDATE SET machine=excluded.machine, state=excluded.state, head=excluded.head,
                  count=excluded.count
                """, new SqlArg("$session", session), new SqlArg("$machine", machine),
                new SqlArg("$state", complete ? "complete" : "downloading"), new SqlArg("$head", facts.Head),
                new SqlArg("$count", facts.Count));
            return new CopyStart(complete, SubagentRows(saved));
        });

    /// <summary>Older items below the copy's first seq. Returns the subagent cards it saved.</summary>
    internal IReadOnlyList<StoredRow> Backfill(string session, IReadOnlyList<StoredRow> rows) =>
        writer.Write("rows.copy-backfill", scope => SubagentRows(Save(scope, session, rows)));

    /// <summary>
    /// Rows the follower already copied forward become a copy still downloading, to the remote's head and with its
    /// count of rows there, which the hole check compares.
    /// </summary>
    internal void Adopt(string session, string machine, HistoryFacts facts) => writer.Write("rows.copy-adopt", scope => scope.Execute("""
        INSERT INTO remote_copy(session_id,machine,state,head,count) VALUES($session,$machine,'downloading',$head,$count)
        ON CONFLICT(session_id) DO UPDATE SET machine=excluded.machine, state='downloading', head=excluded.head,
          count=excluded.count
        """, new SqlArg("$session", session), new SqlArg("$machine", machine), new SqlArg("$head", facts.Head),
        new SqlArg("$count", facts.Count)));

    /// <summary>Rows were imported on the remote below the copy's oldest: it downloads again below it.</summary>
    internal void Reopen(string session, HistoryFacts facts) => writer.Write("rows.copy-reopen", scope =>
    {
        MarkDownloading(scope, session, facts.Head, facts.Count);
        return 0;
    });

    /// <summary>Deletes the copy and everything it saved, in one write, so it downloads again from the top.</summary>
    internal void Drop(string session) => writer.Write("rows.copy-drop", scope =>
    {
        DropCopy(scope, session);
        return 0;
    });

    internal void MarkComplete(string session) => writer.Write("rows.copy-complete", scope => scope.Execute(
        "UPDATE remote_copy SET state='complete' WHERE session_id=$session", new SqlArg("$session", session)));

    internal static IReadOnlyList<StoredRow> SubagentRows(IEnumerable<StoredRow> rows) =>
        [.. rows.Where(row => row.Kind == RowKind.Subagent)];

    private static List<StoredRow> Save(IWriteScope scope, string session, IReadOnlyList<StoredRow> rows)
    {
        var saved = new List<StoredRow>();
        foreach (var row in rows.OrderBy(row => row.Seq))
        {
            // A delta whose message is saved here is already folded into it.
            if (row.Kind == RowKind.Delta && scope.QueryFirst("""
                    SELECT 1 FROM rows WHERE session_id=$session AND item_id=$item AND kind='message'
                    """, _ => true, new SqlArg("$session", session), new SqlArg("$item", row.ItemId)))
                continue;
            if (scope.Execute("""
                    INSERT INTO rows(session_id,seq,run_id,item_id,kind,payload,created_at) VALUES($session,$seq,$run,$item,$kind,$payload,$at)
                    ON CONFLICT DO NOTHING
                    """, new SqlArg("$session", session), new SqlArg("$seq", row.Seq), new SqlArg("$run", row.RunId),
                    new SqlArg("$item", row.ItemId), new SqlArg("$kind", row.Kind), new SqlArg("$payload", row.Payload.GetRawText()),
                    new SqlArg("$at", row.CreatedAt)) == 0)
                continue;
            RowStore.DeleteDeltasOfFinished(scope, session, row.ItemId, row.Kind);
            saved.Add(row);
        }
        return saved;
    }
}
