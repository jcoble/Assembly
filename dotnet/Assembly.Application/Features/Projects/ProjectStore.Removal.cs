using Assembly.Application.Common.Projects;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Projects;

public sealed partial class ProjectStore
{
    public (ProjectRecord Project, FolderInspection Inspection) SetRoot(IWriteScope s, string id, FolderInspection inspection)
    {
        var p = Live(s, id);
        if (s.QueryFirst("SELECT id FROM projects WHERE machine=$machine AND root_path=$root AND id<>$id", r => r.Text(0),
            new SqlArg("$machine", p.Machine), new SqlArg("$root", inspection.RootPath), new SqlArg("$id", id)) is not null)
            throw new CallFailed("Another project already uses that folder.");
        s.Execute("UPDATE projects SET root_path=$root,repo_key=$repo WHERE id=$id", new SqlArg("$root", inspection.RootPath),
            new SqlArg("$repo", inspection.RepoKey), new SqlArg("$id", id));
        Changed(s); return (p with { RootPath = inspection.RootPath, RepoKey = inspection.RepoKey }, inspection);
    }

    public ProjectRecord Remove(IWriteScope s, string id, bool onlyIfEmpty)
    {
        var p = s.QueryFirst($"SELECT {Columns} FROM projects WHERE id=$id", Read, new SqlArg("$id", id))
            ?? throw new KeyNotFoundException("That project is gone.");
        // Task 4 owns session_info.project_id. This consumer is composed only after that migration lands.
        if (onlyIfEmpty && s.QueryFirst("SELECT 1 FROM session_info WHERE project_id=$id LIMIT 1", r => r.Long(0), new SqlArg("$id", id)) != 0)
            throw new CallFailed("Sessions still belong to this project.");
        if (p.RemovedAt is not null) return p;
        var at = Now;
        s.Execute("UPDATE projects SET removed_at=$at WHERE id=$id", new SqlArg("$at", at), new SqlArg("$id", id));
        Changed(s); return p with { RemovedAt = at };
    }
}
