using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Projects;
using Assembly.Tests.Draft.Files;

namespace Assembly.Tests.Infrastructure.Projects;

public sealed class LocalProjectFoldersTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("project-folders-").FullName;
    private readonly LocalProjectFolders _folders = new(new GitCli());
    public void Dispose() => Directory.Delete(_root, true);
    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
    private Task<Assembly.Application.Features.Projects.FolderInspection> Inspect(string path) => _folders.InspectAsync(path, default);
    [Fact]
    public async Task Plain_no_remote_unborn_and_upstream_inspection_are_distinct()
    {
        var plain = Folder("plain"); Assert.False((await Inspect(plain)).IsGit);
        var repo = Folder("no-remote"); GitFolder.Run(repo, "init", "-b", "main");
        var unborn = await Inspect(repo);
        Assert.True(unborn.IsGit); Assert.True(unborn.Unborn); Assert.Null(unborn.RepoKey); Assert.Equal("no-remote", unborn.Title);
        GitFolder.Run(repo, "remote", "add", "origin", "https://github.com/origin/repo.git");
        GitFolder.Run(repo, "remote", "add", "upstream", "git@GitHub.com:up/repo.git");
        Assert.Equal("github.com/up/repo", (await Inspect(repo)).RepoKey);
    }
    [Fact]
    public async Task Symlinked_ancestor_and_trailing_slash_paths_are_one_root_including_newlines()
    {
        var actual = Folder("spaces and ü\n"); var child = Directory.CreateDirectory(Path.Combine(actual, "child")).FullName;
        var link = Path.Combine(_root, "link"); Directory.CreateSymbolicLink(link, actual);
        Assert.Equal((await Inspect(child)).RootPath, (await Inspect(Path.Combine(link, "child") + "/")).RootPath);
    }
    [Fact]
    public async Task Parent_segments_follow_the_resolved_symlink_target()
    {
        var parent = Folder("parent"); var target = Directory.CreateDirectory(Path.Combine(parent, "target")).FullName;
        var link = Path.Combine(_root, "link"); Directory.CreateSymbolicLink(link, target);
        Assert.Equal(parent, (await Inspect(link + "/..")).RootPath);
    }
    [Fact]
    public async Task Bad_folders_are_refused_with_the_exact_reason()
    {
        var repo = Folder("repo"); GitFolder.Run(repo, "init", "-b", "main");
        var nested = Directory.CreateDirectory(Path.Combine(repo, "nested")).FullName;
        var bare = Folder("bare"); GitFolder.Run(bare, "init", "--bare");
        var file = Path.Combine(_root, "file"); File.WriteAllText(file, "kept");
        foreach (var (path, reason) in new[] {
            (bare, "This is a bare repository, so it has no working files. Choose one of its checkouts instead."),
            (Path.Combine(repo, ".git"), "This is git's internal folder. Choose the repository folder instead."),
            (nested, $"This folder is inside the repository at {repo}. Choose that folder instead."),
            (file, "That path is a file. Choose a folder."),
            (Path.Combine(_root, "missing"), "That folder does not exist."),
            ("relative", "Folder path must be absolute") })
            Assert.Equal(reason, (await Assert.ThrowsAsync<CallFailed>(() => Inspect(path))).Message);
    }
    [Fact]
    public async Task Listing_stops_at_500_and_empty_folder_is_valid()
    {
        var empty = Folder("empty"); Assert.Empty((await _folders.ListAsync(empty, default)).Directories);
        for (var i = 0; i < 501; i++) Directory.CreateDirectory(Path.Combine(empty, $"dir-{i:000}"));
        var listing = await _folders.ListAsync(empty, default);
        Assert.Equal(500, listing.Directories.Count); Assert.True(listing.Truncated);
        Assert.All(listing.Directories, p => Assert.True(Path.IsPathFullyQualified(p)));
        Assert.Equal(listing.Directories.Order(StringComparer.Ordinal), listing.Directories);
    }
    [Fact]
    public async Task A_trailing_separator_lists_real_children_and_the_selected_next_level()
    {
        var parent = Folder("browse");
        var child = Directory.CreateDirectory(Path.Combine(parent, "next directory")).FullName;
        var grandchild = Directory.CreateDirectory(Path.Combine(child, "deeper")).FullName;
        File.WriteAllText(Path.Combine(parent, "not-a-directory"), "kept");
        var plain = await _folders.ListAsync(parent, default);
        var slash = await _folders.ListAsync(parent + "/", default);
        Assert.Equal(parent, slash.Path); Assert.Equal(plain.Directories, slash.Directories);
        Assert.Equal([child], slash.Directories); Assert.False(slash.Truncated);
        var next = await _folders.ListAsync(slash.Directories[0] + "/", default);
        Assert.Equal(child, next.Path); Assert.Equal([grandchild], next.Directories);
        var alias = Path.Combine(_root, "browse-link"); Directory.CreateSymbolicLink(alias, parent);
        var linked = await _folders.ListAsync(alias + "/", default);
        Assert.Equal(parent, linked.Path); Assert.Equal(slash.Directories, linked.Directories);
    }
    [Fact]
    public async Task A_filesystem_root_stays_a_root_when_its_children_are_listed()
    {
        var root = Path.GetPathRoot(_root)!;
        var listing = await _folders.ListAsync(root, default);
        Assert.Equal(root, listing.Path);
        Assert.All(listing.Directories, child => Assert.Equal(root, Path.GetDirectoryName(child)));
    }
    [Fact]
    public async Task Linked_worktree_is_valid_and_refs_name_its_held_branch_without_mutation()
    {
        var repo = Folder("repo\n"); File.WriteAllText(Path.Combine(repo, "kept"), "original");
        GitFolder.Run(repo, "init", "-b", "main"); GitFolder.Commit(repo);
        GitFolder.Run(repo, "branch", "other"); GitFolder.Run(repo, "branch", "z");
        var linked = Path.Combine(_root, "linked space\n"); GitFolder.Run(repo, "worktree", "add", linked, "other");
        Assert.Equal((await Inspect(repo)).CommonDir, (await Inspect(linked)).CommonDir);
        var before = await GitText(repo, "show-ref"); var trees = await GitText(repo, "worktree", "list", "--porcelain", "-z");
        var first = await _folders.RefsAsync(repo, null, null, null, 2, default);
        Assert.Equal(2, first.Refs.Count); Assert.NotNull(first.Next);
        Assert.Equal(linked, first.Refs.Single(r => r.Name == "refs/heads/other").CheckedOutPath);
        var second = await _folders.RefsAsync(repo, null, null, first.Next, 2, default);
        Assert.Single(second.Refs); Assert.Null(second.Next);
        Assert.Equal(before, await GitText(repo, "show-ref")); Assert.Equal(trees, await GitText(repo, "worktree", "list", "--porcelain", "-z"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(repo, "kept")));
    }
    [Fact]
    public async Task Detached_and_unborn_current_have_honest_head_and_branch()
    {
        var repo = Folder("repo"); GitFolder.Run(repo, "init", "-b", "main");
        var unborn = await _folders.RefsAsync(repo, null, null, null, 100, default);
        Assert.Equal("main", unborn.Current.Branch); Assert.Null(unborn.Current.Head);
        File.WriteAllText(Path.Combine(repo, "kept"), "x"); GitFolder.Commit(repo); GitFolder.Run(repo, "checkout", "--detach");
        var detached = await _folders.RefsAsync(repo, null, null, null, 100, default);
        Assert.Null(detached.Current.Branch); Assert.NotNull(detached.Current.Head);
    }
    [Fact]
    public async Task Same_short_name_in_branch_remote_and_tag_pages_each_canonical_identity_once()
    {
        var repo = Folder("collision"); GitFolder.Run(repo, "init", "-b", "same");
        File.WriteAllText(Path.Combine(repo, "kept"), "x"); GitFolder.Commit(repo);
        GitFolder.Run(repo, "tag", "same"); GitFolder.Run(repo, "update-ref", "refs/remotes/same", "HEAD");
        var all = new List<Assembly.Application.Features.Projects.GitRef>(); string? after = null;
        do
        {
            var page = await _folders.RefsAsync(repo, null, "same", after, 1, default);
            Assert.Single(page.Refs); all.AddRange(page.Refs); after = page.Next;
        } while (after is not null);
        Assert.Equal(["refs/heads/same", "refs/remotes/same", "refs/tags/same"], all.Select(r => r.Name));
        Assert.Equal(["branch", "remote", "tag"], all.Select(r => r.Kind));
    }
    [Fact]
    public async Task A_cancelled_read_starts_no_git_and_returns_no_facts()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _folders.InspectAsync(_root, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _folders.ListAsync(_root, cancelled.Token));
    }
    private static async Task<string> GitText(string root, params string[] args)
    {
        var records = new List<string>();
        await foreach (var record in new GitCli().ReadAsync(root, args, null, default)) records.Add(record);
        return string.Join('\0', records);
    }
}
