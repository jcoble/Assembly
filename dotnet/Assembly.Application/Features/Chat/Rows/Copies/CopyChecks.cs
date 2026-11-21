using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Chat.Rows.Copies;

/// <summary>
/// What the one hole check compares (TSK-1692): the remote's count of rows up to the copy's head, recorded when the
/// download started, against the rows saved here up to the same head, and where seqs jump.
/// </summary>
public sealed partial class CopyRecords
{
    /// <summary>The remote's count of rows other than deltas up to the copy's head, or null when none was recorded.</summary>
    public long? RemoteCount(string session) => db.QueryFirst(
        "SELECT count FROM remote_copy WHERE session_id=$session", row => row.IsNull(0) ? (long?)null : row.Long(0),
        CancellationToken.None, new SqlArg("$session", session));

    /// <summary>The rows other than deltas saved here up to <paramref name="head"/>. Deltas are left out on both sides.</summary>
    public long SavedCount(string session, long head) => db.QueryFirst(
        "SELECT COUNT(*) FROM rows WHERE session_id=$session AND seq<=$head AND kind<>'delta'", row => row.Long(0),
        CancellationToken.None, new SqlArg("$session", session), new SqlArg("$head", head));

    /// <summary>
    /// Each place up to <paramref name="head"/> where the saved seqs skip: the seq before the gap and the one after it,
    /// counting from 0 at the start and to <c>head + 1</c> at the end. Trimmed deltas leave such gaps too.
    /// </summary>
    public IReadOnlyList<(long From, long To)> Jumps(string session, long head) => db.Query("""
        SELECT prev, seq FROM (
          SELECT seq, LAG(seq, 1, 0) OVER (ORDER BY seq) AS prev FROM rows WHERE session_id=$session AND seq<=$head)
        WHERE seq-prev>1
        UNION ALL
        SELECT COALESCE(MAX(seq), 0), $head+1 FROM rows WHERE session_id=$session AND seq<=$head
        HAVING COALESCE(MAX(seq), 0) < $head
        ORDER BY 1
        """, row => (row.Long(0), row.Long(1)), CancellationToken.None, new SqlArg("$session", session), new SqlArg("$head", head));
}
