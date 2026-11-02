using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Projects;
using Assembly.Tests.Draft.Files;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class RemoteFoldersTests
{
    [Fact]
    public async Task Folder_inspection_refs_listing_and_worktrees_use_the_authenticated_owner_socket()
    {
        using var repo = new GitFolder("keep"); GitFolder.Commit(repo.Root);
        GitFolder.Run(repo.Root, "remote", "add", "origin", "https://github.com/test/owner.git");
        await using var hosts = new TwoHosts();
        var reads = new CountingFolders(new LocalProjectFolders(new GitCli()));
        hosts.Remote.AdditionalFeatures = calls => new FoldersFeature(reads).Register(calls);
        await hosts.Start(); await hosts.StartMirror();
        var forward = new MachineForward(machine => machine == hosts.Remote.Runtime.Machine ? hosts.ConnectedMachine : null);
        var remote = new RemoteProjectFolders(forward, hosts.Remote.Runtime.Machine);
        var inspection = await remote.InspectAsync(repo.Root, default);
        Assert.Equal(repo.Root, inspection.RootPath); Assert.Equal("github.com/test/owner", inspection.RepoKey);
        Assert.Equal(repo.Root, (await remote.ListAsync(repo.Root, default)).Path);
        Assert.NotEmpty((await remote.RefsAsync(repo.Root, null, null, null, 100, default)).Refs);
        Assert.Single(await remote.WorktreesAsync(repo.Root, default));
        Assert.Equal(["inspect", "list", "refs", "worktrees"], reads.Calls);
    }
    [Fact]
    public async Task Disconnected_owner_fails_unreachable_without_registry_or_local_fallback()
    {
        var forward = new MachineForward(_ => null); var remote = new RemoteProjectFolders(forward, "workbox-test");
        var error = await Assert.ThrowsAsync<MachineUnreachable>(() => remote.InspectAsync("/exists-only-on-owner", default));
        Assert.Equal("workbox-test", error.Machine);
    }
    private sealed class CountingFolders(IProjectFolders inner) : IProjectFolders
    {
        public List<string> Calls { get; } = [];
        public Task<FolderListing> ListAsync(string path, CancellationToken ct) { Calls.Add("list"); return inner.ListAsync(path, ct); }
        public Task<FolderInspection> InspectAsync(string path, CancellationToken ct) { Calls.Add("inspect"); return inner.InspectAsync(path, ct); }
        public Task<RefPage> RefsAsync(string root, string? checkout, string? filter, string? after, int limit, CancellationToken ct)
        { Calls.Add("refs"); return inner.RefsAsync(root, checkout, filter, after, limit, ct); }
        public Task<IReadOnlyList<Assembly.Application.Common.Git.Worktree>> WorktreesAsync(string root, CancellationToken ct)
        { Calls.Add("worktrees"); return inner.WorktreesAsync(root, ct); }
    }
}
