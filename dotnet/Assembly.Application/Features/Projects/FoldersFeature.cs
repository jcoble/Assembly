using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Projects;

/// <summary>Machine-local reads, registered on each owning host.</summary>
internal sealed class FoldersFeature(IProjectFolders folders) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        calls.Read("folders", "list", ProjectsJson.Default.FolderPathArgs, ProjectsJson.Default.FolderListing,
            (a, c) => folders.ListAsync(a.Path, c.Cancellation));
        calls.Read("folders", "inspect", ProjectsJson.Default.FolderPathArgs, ProjectsJson.Default.FolderInspection,
            (a, c) => folders.InspectAsync(a.Path, c.Cancellation));
        calls.Read("folders", "refs", ProjectsJson.Default.FolderRefsArgs, ProjectsJson.Default.RefPage,
            (a, c) => folders.RefsAsync(a.Root, a.CheckoutPath, a.Filter, a.After, a.Limit, c.Cancellation));
        calls.Read("folders", "worktrees", ProjectsJson.Default.FolderRootArgs, ProjectsJson.Default.FolderWorktrees,
            async (a, c) => new FolderWorktrees(await folders.WorktreesAsync(a.Root, c.Cancellation)));
    }
}
