using Assembly.Application.Common.Database;

namespace Assembly.Engine.Sessions;

public sealed record DirectoryEntry(string SessionId, string Title, string Provider, string? Model,
    string State, string Relation, string Machine);

/// <summary>Local session discovery. A listed relationship does not grant command authority.</summary>
public static class SessionDirectory
{
    private const string Candidates = """
        WITH candidates AS (
            SELECT s.id,s.title,s.provider,s.model,s.state,s.machine,s.updated_ms,
                CASE
                    WHEN s.id=c.parent_id THEN 'starter'
                    WHEN s.parent_id=c.id THEN 'child'
                    WHEN c.parent_id IS NOT NULL AND s.parent_id=c.parent_id THEN 'sibling'
                    WHEN s.parent_id IS NULL THEN 'other'
                END AS relation
            FROM engine_session c JOIN engine_session s ON s.machine=c.machine
            WHERE c.id=$caller AND s.id!=c.id AND s.state!='ended'
        )
        """;

    public static IReadOnlyList<DirectoryEntry> Reachable(IReadScope scope, string callerId) =>
        scope.Query(Candidates + """
            SELECT id,title,provider,model,state,relation,machine FROM candidates
            WHERE relation IS NOT NULL ORDER BY relation,updated_ms DESC LIMIT 200
            """, r => new DirectoryEntry(r.Text(0), r.Text(1), r.Text(2), r.IsNull(3) ? null : r.Text(3), r.Text(4), r.Text(5), r.Text(6)),
            new SqlArg("$caller", callerId));

    public static bool CanReach(IReadScope scope, string fromId, string toId) =>
        scope.QueryFirst(Candidates + """
            SELECT COUNT(*) FROM candidates WHERE id=$target AND relation IS NOT NULL
            """, r => r.Long(0), new("$caller", fromId), new("$target", toId)) != 0;
}
