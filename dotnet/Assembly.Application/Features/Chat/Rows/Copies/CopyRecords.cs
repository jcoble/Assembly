using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Application.Features.Chat.Rows.Copies;

/// <summary>Whether a remote session's rows here are a saved copy, whose machine it is, and whether it is whole.</summary>
public sealed record RemoteCopy(string Session, string Machine, bool Complete, long? Head);

/// <summary>
/// The saved copy of a remote session (TSK-1692). A copy belongs to the owning machine's engine id only, never to a
/// mirror generation. Its oldest and last seqs are its rows' own, never stored.
/// </summary>
public sealed class CopyRecords(IAppDatabase db, IAtomicWriter writer)
{
    /// <summary>
    /// Each remote session's owning engine machine: its mirrored session row at the mirror's current generation, or,
    /// for a facade with no mirrored row (a subagent child), the machine its route name names. A session whose engine
    /// record names another machine has none.
    /// </summary>
    internal const string RemoteOwners = """
        remote_owners AS (
          SELECT r.entity_id AS session_id, r.machine FROM engine_mirror_row r
          JOIN engine_mirror m ON m.machine=r.machine AND m.generation=r.generation AND m.state<>'retired'
          WHERE r.entity_kind='session'
          UNION
          SELECT s.session_id, m.machine FROM session_info s JOIN engine_mirror m ON m.name=s.machine AND m.state<>'retired'
          WHERE length(s.machine)>0 AND NOT EXISTS (
            SELECT 1 FROM engine_mirror_row r WHERE r.entity_kind='session' AND r.entity_id=s.session_id)),
        owned AS (
          SELECT o.session_id, MIN(o.machine) AS machine FROM remote_owners o
          WHERE o.machine <> (SELECT machine_id FROM engine_meta WHERE singleton=1)
            AND NOT EXISTS (SELECT 1 FROM engine_session e WHERE e.id=o.session_id AND e.machine<>o.machine)
          GROUP BY o.session_id HAVING COUNT(DISTINCT o.machine)=1)
        """;

    public RemoteCopy? RemoteCopyOf(string session) => db.QueryFirst(
        "SELECT session_id,machine,state,head FROM remote_copy WHERE session_id=$session", ReadCopy, CancellationToken.None,
        new SqlArg("$session", session));

    public IReadOnlyList<RemoteCopy> RemoteCopiesOn(string machine) => db.Query(
        "SELECT session_id,machine,state,head FROM remote_copy WHERE machine=$machine ORDER BY session_id", ReadCopy,
        CancellationToken.None, new SqlArg("$machine", machine));

    /// <summary>The copy's oldest saved seq, or null when it holds no rows.</summary>
    public long? FirstSeq(string session) => db.QueryFirst(
        "SELECT MIN(seq) FROM rows WHERE session_id=$session", row => row.IsNull(0) ? (long?)null : row.Long(0),
        CancellationToken.None, new SqlArg("$session", session));

    internal static void MarkDownloading(IWriteScope scope, string session, long head) => scope.Execute(
        "UPDATE remote_copy SET state='downloading', head=$head WHERE session_id=$session",
        new SqlArg("$session", session), new SqlArg("$head", head));

    /// <summary>Deletes everything the copy saved, in the caller's write. The session's own record stays.</summary>
    internal static void DropCopy(IWriteScope scope, string session)
    {
        foreach (var table in new[] { "rows", "tool_details", "tool_terminal_refs", "acp_terminal_details", "sessions", "remote_copy" })
            scope.Execute($"DELETE FROM {table} WHERE session_id=$session", new SqlArg("$session", session));
    }

    /// <summary>
    /// An install from before saved copies holds forward copies, each a prefix from seq 1: each becomes a copy still
    /// downloading, for its owner, so the one hole check validates it. Run at startup; a second run adopts nothing.
    /// </summary>
    public int AdoptRemoteCopies() => writer.Write("rows.adopt-copies", scope => scope.Execute($"""
        WITH {RemoteOwners}
        INSERT INTO remote_copy(session_id,machine,state,head)
        SELECT o.session_id, o.machine, 'downloading', NULL FROM owned o
        WHERE EXISTS (SELECT 1 FROM rows WHERE rows.session_id=o.session_id)
          AND NOT EXISTS (SELECT 1 FROM remote_copy c WHERE c.session_id=o.session_id)
        """));

    private static RemoteCopy ReadCopy(IRow row) =>
        new(row.Text(0), row.Text(1), row.Text(2) == "complete", row.IsNull(3) ? null : row.Long(3));
}
