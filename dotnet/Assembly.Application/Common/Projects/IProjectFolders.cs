using Assembly.Application.Common.Git;

namespace Assembly.Application.Common.Projects;

public interface IProjectFolders
{
    Task<FolderListing> ListAsync(string path, CancellationToken ct);
    Task<FolderInspection> InspectAsync(string path, CancellationToken ct);
    Task<RefPage> RefsAsync(string root, string? checkoutPath, string? filter, string? after, int limit, CancellationToken ct);
    Task<IReadOnlyList<Worktree>> WorktreesAsync(string root, CancellationToken ct);
}
