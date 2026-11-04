using Assembly.Application.Common.Projects;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Git;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Projects;

/// <summary>Typed folder reads on the existing owning-machine socket.</summary>
internal sealed class RemoteProjectFolders(RemoteConnections connections, string savedName) : IProjectFolders
{
    public Task<FolderListing> ListAsync(string path, CancellationToken ct) =>
        CallAsync("list", new FolderPathArgs(path), ProjectsJson.Default.FolderPathArgs, ProjectsJson.Default.FolderListing, ct);
    public Task<FolderInspection> InspectAsync(string path, CancellationToken ct) =>
        CallAsync("inspect", new FolderPathArgs(path), ProjectsJson.Default.FolderPathArgs, ProjectsJson.Default.FolderInspection, ct);
    public Task<RefPage> RefsAsync(string root, string? checkoutPath, string? filter, string? after, int limit, CancellationToken ct) =>
        CallAsync("refs", new FolderRefsArgs(root, checkoutPath, filter, after, limit), ProjectsJson.Default.FolderRefsArgs, ProjectsJson.Default.RefPage, ct);
    public async Task<IReadOnlyList<Worktree>> WorktreesAsync(string root, CancellationToken ct) =>
        (await CallAsync("worktrees", new FolderRootArgs(root), ProjectsJson.Default.FolderRootArgs, ProjectsJson.Default.FolderWorktrees, ct)).Worktrees;
    private async Task<TResult> CallAsync<TArgs, TResult>(string method, TArgs args, JsonTypeInfo<TArgs> input, JsonTypeInfo<TResult> output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (connections.StartupSnapshot(savedName) is null)
            throw new MachineUnreachable(savedName, $"{savedName}: machine socket is disconnected");
        var owner = await connections.CaptureProjectOwnerAsync(savedName, ct);
        connections.RequireProjectOwner(owner, ct);
        var result = await owner.Machine.CallAsync("folders", method, JsonSerializer.SerializeToElement(args, input), false, ct, owner.Engine, owner.Socket);
        connections.RequireProjectOwner(owner, ct);
        return result.Deserialize(output) ?? throw new IOException("Invalid folder result from the owning machine.");
    }
}
