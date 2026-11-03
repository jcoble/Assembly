using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;

namespace Assembly.Engine.Mcp;

public static class WorkflowToolProjects
{
    public static ProjectRecord Resolve(IReadScope scope, string machine, string callerSessionId, string? project)
    {
        var matches = scope.Query("""
            SELECT p.id,p.machine,p.root_path,p.title,p.repo_key,p.pinned_at,p.removed_at,p.created_at,p.last_used_at
            FROM projects p WHERE p.machine=$machine AND p.removed_at IS NULL AND (
              ($project IS NOT NULL AND (p.root_path=$project OR p.title=$project)) OR
              ($project IS NULL AND p.id=(SELECT project_id FROM engine_session WHERE id=$session)))
            LIMIT 2
            """, r => new ProjectRecord(r.Text(0), r.Text(1), r.Text(2), r.Text(3), r.IsNull(4) ? null : r.Text(4),
                r.IsNull(5) ? null : r.Long(5), r.IsNull(6) ? null : r.Long(6), r.Long(7), r.IsNull(8) ? null : r.Long(8)),
            new SqlArg("$machine", machine), new("$session", callerSessionId), new("$project", project));
        if (matches.Count != 1) throw new McpRefused(new("bad_args", new Dictionary<string, string>
        { ["field"] = "project", ["problem"] = matches.Count == 0 ? "must name an already-added project by exact root or title" : "is ambiguous; use its exact root" }));
        return matches[0];
    }
}
