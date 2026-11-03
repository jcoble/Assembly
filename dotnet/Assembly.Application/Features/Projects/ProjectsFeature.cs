using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Projects;

/// <summary>The desktop registry; filesystem reads precede its single atomic write.</summary>
internal sealed class ProjectsFeature(ProjectStore store, Func<string, IProjectFolders?> foldersOn, IAppDatabase db) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Topic("projects", ProjectsJson.Default.ProjectsRevision, _ => "revision",
            (_, c) => [new ProjectsRevision(db.QueryFirst("SELECT revision FROM projects_meta", r => r.Long(0), c.Cancellation))]);
        calls.Read("projects", "list", ProjectsJson.Default.ProjectListArgs, ProjectsJson.Default.ProjectPage,
            (a, c) => Task.FromResult(store.List(a.Filter, a.Machine, a.After, a.Limit, c.Cancellation)));
        calls.Read("projects", "recentParent", ProjectsJson.Default.ProjectMachineArgs, ProjectsJson.Default.ProjectRecentParent,
            (a, c) => Task.FromResult(new ProjectRecentParent(store.RecentParent(a.Machine, c.Cancellation))));
        calls.Read("projects", "folders", ProjectsJson.Default.ProjectPathArgs, ProjectsJson.Default.FolderListing,
            (a, c) => Folders(a.Machine).ListAsync(a.Path, c.Cancellation));
        calls.Read("projects", "inspect", ProjectsJson.Default.ProjectIdArgs, ProjectsJson.Default.FolderInspection,
            (a, c) => { var p = Live(a.ProjectId); return Folders(p.Machine).InspectAsync(p.RootPath, c.Cancellation); });
        calls.Read("projects", "refs", ProjectsJson.Default.ProjectRefsArgs, ProjectsJson.Default.RefPage,
            (a, c) => { var p = Live(a.ProjectId); return Folders(p.Machine).RefsAsync(p.RootPath, a.CheckoutPath, a.Filter, a.After, a.Limit, c.Cancellation); });
        calls.Write("projects", "add", ProjectsJson.Default.ProjectPathArgs, ProjectsJson.Default.ProjectAddResult, async (a, c) =>
        {
            var inspected = await Folders(a.Machine).InspectAsync(a.Path, c.Cancellation);
            return s =>
            {
                var main = inspected.CommonDir is { } common && common != Path.Combine(inspected.RootPath, ".git")
                    ? store.AtRoot(s, a.Machine, Path.GetDirectoryName(common)!) : null;
                if (main is { RemovedAt: null }) return new(main, inspected, true);
                var result = store.AddOrRestore(s, a.Machine, inspected.RootPath, inspected.Title, inspected.RepoKey);
                return new(result.Project, inspected, result.AlreadyExisted);
            };
        }, a => $"machine {a.Machine}");
        calls.Write("projects", "rename", ProjectsJson.Default.ProjectRenameArgs, ProjectsJson.Default.ProjectResult,
            (a, _) => Task.FromResult<Func<IWriteScope, ProjectResult>>(s => new(store.Rename(s, a.Id, a.Title))));
        calls.Write("projects", "pin", ProjectsJson.Default.ProjectPinArgs, ProjectsJson.Default.ProjectResult,
            (a, _) => Task.FromResult<Func<IWriteScope, ProjectResult>>(s => new(store.Pin(s, a.Id, a.Pinned))));
        calls.Write("projects", "setRoot", ProjectsJson.Default.ProjectSetRootArgs, ProjectsJson.Default.ProjectSetRootResult, async (a, c) =>
        {
            var p = Live(a.Id);
            var inspected = await Folders(p.Machine).InspectAsync(a.Path, c.Cancellation);
            return s => { var r = store.SetRoot(s, a.Id, inspected); return new(r.Project, r.Inspection); };
        });
        calls.Write("projects", "remove", ProjectsJson.Default.ProjectRemoveArgs, ProjectsJson.Default.ProjectResult,
            (a, _) => Task.FromResult<Func<IWriteScope, ProjectResult>>(s => new(store.Remove(s, a.Id, a.OnlyIfEmpty))));
    }
    private ProjectRecord Live(string id) => store.Get(id) is { RemovedAt: null } p ? p : throw new KeyNotFoundException("That project is gone.");
    private IProjectFolders Folders(string machine) => foldersOn(machine) ?? throw new KeyNotFoundException("That machine is unavailable.");
}
