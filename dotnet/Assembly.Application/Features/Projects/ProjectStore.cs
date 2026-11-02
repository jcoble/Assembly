using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Projects;

/// <summary>Desktop folder identities. Every mutation uses the caller's one transaction.</summary>
public sealed partial class ProjectStore
{
    private const string Columns = "id,machine,root_path,title,repo_key,pinned_at,removed_at,created_at,last_used_at";
    private readonly IAppDatabase _db;
    private readonly TimeProvider _clock;
    public ProjectStore(IAppDatabase db, TimeProvider clock)
    {
        (_db, _clock) = (db, clock);
        db.Writer.Write("projects.schema", s => s.Execute(ProjectsSchema.Create));
    }
    public ProjectRecord? Get(string id) => _db.QueryFirst($"SELECT {Columns} FROM projects WHERE id=$id", Read,
        CancellationToken.None, new SqlArg("$id", id));
    public ProjectRecord? AtRoot(string machine, string rootPath) => _db.QueryFirst(
        $"SELECT {Columns} FROM projects WHERE machine=$machine AND root_path=$root", Read,
        CancellationToken.None, new SqlArg("$machine", machine), new SqlArg("$root", rootPath));
    internal ProjectRecord? AtRoot(IWriteScope s, string machine, string rootPath) => s.QueryFirst(
        $"SELECT {Columns} FROM projects WHERE machine=$machine AND root_path=$root", Read,
        new SqlArg("$machine", machine), new SqlArg("$root", rootPath));
    public long Revision(CancellationToken ct) => _db.QueryFirst("SELECT revision FROM projects_meta", r => r.Long(0), ct);

    public ProjectPage List(string? filter, string? machine, ProjectCursor? after, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw new CallBadArguments("limit must be between 1 and 100.");
        var rows = _db.Query($"""
            SELECT {Columns} FROM projects
            WHERE removed_at IS NULL AND ($filter IS NULL OR instr(lower(title),lower($filter))>0 OR instr(lower(root_path),lower($filter))>0)
              AND ($machine IS NULL OR machine=$machine)
              AND ($after=0 OR (pinned_at IS NOT NULL)<$pinned
                OR ((pinned_at IS NOT NULL)=$pinned AND (
                  ($used IS NOT NULL AND (last_used_at IS NULL OR last_used_at<$used))
                  OR (last_used_at IS $used AND (created_at>$created OR (created_at=$created AND id>$id))))))
            ORDER BY pinned_at IS NULL,last_used_at DESC,created_at,id LIMIT $limit
            """, Read, ct, new SqlArg("$filter", filter), new SqlArg("$machine", machine), new SqlArg("$after", after is null ? 0 : 1),
            new SqlArg("$pinned", after?.Pinned ?? 0), new SqlArg("$used", after?.LastUsedAt),
            new SqlArg("$created", after?.CreatedAt ?? 0), new SqlArg("$id", after?.Id ?? ""), new SqlArg("$limit", limit + 1));
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        var last = rows.LastOrDefault();
        return new(rows, more && last is not null ? new(last.PinnedAt is null ? 0 : 1, last.LastUsedAt, last.CreatedAt, last.Id) : null);
    }

    public (ProjectRecord Project, bool AlreadyExisted) AddOrRestore(IWriteScope s, string machine, string rootPath, string title, string? repoKey)
    {
        var existing = s.QueryFirst($"SELECT {Columns} FROM projects WHERE machine=$machine AND root_path=$root", Read,
            new SqlArg("$machine", machine), new SqlArg("$root", rootPath));
        if (existing is not null)
        {
            if (existing.RemovedAt is null) return (existing, true);
            s.Execute("UPDATE projects SET removed_at=NULL,repo_key=$repo WHERE id=$id", new SqlArg("$repo", repoKey), new SqlArg("$id", existing.Id));
            Changed(s); return (existing with { RemovedAt = null, RepoKey = repoKey }, true);
        }
        var project = new ProjectRecord(Guid.NewGuid().ToString("D"), machine, rootPath, title, repoKey, null, null, Now, null);
        s.Execute("INSERT INTO projects(id,machine,root_path,title,repo_key,created_at) VALUES($id,$machine,$root,$title,$repo,$at)",
            new SqlArg("$id", project.Id), new SqlArg("$machine", machine), new SqlArg("$root", rootPath),
            new SqlArg("$title", title), new SqlArg("$repo", repoKey), new SqlArg("$at", project.CreatedAt));
        Changed(s); return (project, false);
    }

    public ProjectRecord Rename(IWriteScope s, string id, string title)
    {
        title = title.Trim();
        if (title.Length == 0) throw new CallBadArguments("A project title is required.");
        var p = Live(s, id);
        s.Execute("UPDATE projects SET title=$title WHERE id=$id", new SqlArg("$title", title), new SqlArg("$id", id));
        Changed(s); return p with { Title = title };
    }
    public ProjectRecord Pin(IWriteScope s, string id, bool pinned)
    {
        var p = Live(s, id); var at = pinned ? p.PinnedAt ?? Now : (long?)null;
        s.Execute("UPDATE projects SET pinned_at=$at WHERE id=$id", new SqlArg("$at", at), new SqlArg("$id", id));
        Changed(s); return p with { PinnedAt = at };
    }
    public void Touch(IWriteScope s, string id)
    {
        _ = Live(s, id);
        s.Execute("UPDATE projects SET last_used_at=$at WHERE id=$id", new SqlArg("$at", Now), new SqlArg("$id", id));
        Changed(s);
    }
    private long Now => _clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static ProjectRecord Live(IWriteScope s, string id)
    {
        var p = s.QueryFirst($"SELECT {Columns} FROM projects WHERE id=$id", Read, new SqlArg("$id", id));
        return p is null || p.RemovedAt is not null ? throw new KeyNotFoundException("That project is gone.") : p;
    }
    private static void Changed(IWriteScope s)
    {
        s.Execute("UPDATE projects_meta SET revision=revision+1");
        var revision = s.QueryFirst("SELECT revision FROM projects_meta", r => r.Long(0));
        s.Publish("projects", "revision", new ProjectsRevision(revision), ProjectsJson.Default.ProjectsRevision);
    }
    private static ProjectRecord Read(IRow r) => new(r.Text(0), r.Text(1), r.Text(2), r.Text(3),
        r.IsNull(4) ? null : r.Text(4), r.IsNull(5) ? null : r.Long(5), r.IsNull(6) ? null : r.Long(6),
        r.Long(7), r.IsNull(8) ? null : r.Long(8));
}
