using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Worktrees;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.Worktrees;

public sealed class WorktreeReaderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "assembly-worktrees-" + Guid.NewGuid());
    private readonly GitCli _git = new();
    private string Root => Path.Combine(_temp, "main");
    private Task<string> Git(string folder, params string[] arguments) => _git.RunAsync(folder, arguments, CancellationToken.None);
    private Task<IReadOnlyList<Worktree>> Read(string? root = null) => new WorktreeReader(_git).ReadAsync(root ?? Root, CancellationToken.None);

    private async Task Init(bool commit = true)
    {
        Directory.CreateDirectory(Root);
        await Git(Root, "init", "-b", "main");
        await Git(Root, "config", "user.name", "Worktree Test");
        await Git(Root, "config", "user.email", "worktree@example.invalid");
        if (commit) await Commit("First subject");
    }

    private Task<string> Commit(string subject) => Git(Root, "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", subject);

    // Pins: scm#1, scm#3
    [Fact]
    public async Task Main_is_first_with_linked_branch_and_detached_head()
    {
        await Init();
        var linked = Path.Combine(_temp, "linked with spaces");
        var detached = Path.Combine(_temp, "detached");
        await Git(Root, "worktree", "add", "-b", "feature/x", linked);
        await Git(Root, "worktree", "add", "--detach", detached);
        var rows = await Read();
        Assert.Equal(3, rows.Count);
        Assert.Equal(Root, rows[0].Path);
        Assert.True(rows[0].IsMain);
        Assert.Equal("main", rows[0].Branch);
        var branch = Assert.Single(rows, row => row.Path == linked);
        Assert.Equal("feature/x", branch.Branch);
        Assert.False(branch.IsMain);
        var detachedRow = Assert.Single(rows, row => row.Path == detached);
        Assert.Null(detachedRow.Branch);
        Assert.Equal((await Git(Root, "rev-parse", "HEAD")).Trim(), detachedRow.Head);
        Assert.False(detachedRow.IsMain);
    }

    // Pins: scm#1, scm#2
    [Fact]
    public async Task Unborn_branch_has_no_head_or_unpushed_commits()
    {
        await Init(commit: false);
        var row = Assert.Single(await Read());
        Assert.Null(row.Head);
        Assert.Equal(0, row.Unpushed);
        Assert.Equal("main", row.Branch);
        Assert.Null(row.HeadSubject);
        Assert.Null(row.HeadCommittedAt);
    }

    // Pins: scm#2
    [Fact]
    public async Task Untracked_files_count_as_dirty_and_clean_worktrees_do_not()
    {
        await Init();
        Assert.False(Assert.Single(await Read()).IsDirty);
        await File.WriteAllTextAsync(Path.Combine(Root, "untracked"), "new content");
        Assert.True(Assert.Single(await Read()).IsDirty);
    }

    // Pins: scm#2
    [Fact]
    public async Task Counts_only_commits_missing_from_all_remotes()
    {
        await Init();
        var origin = Path.Combine(_temp, "origin");
        await Git(_temp, "init", "--bare", origin);
        await Git(Root, "remote", "add", "origin", origin);
        await Git(Root, "push", "origin", "main");
        await Commit("Second");
        await Commit("Third");
        Assert.Equal(2, Assert.Single(await Read()).Unpushed);
        await Git(Root, "push", "origin", "main");
        Assert.Equal(0, Assert.Single(await Read()).Unpushed);
    }

    // Pins: scm#2
    [Fact]
    public async Task Locked_worktree_reads_even_when_its_folder_is_gone()
    {
        await Init();
        var linked = Path.Combine(_temp, "locked");
        await Git(Root, "worktree", "add", "-b", "locked", linked);
        await Git(Root, "worktree", "lock", "--reason", "external disk", linked);
        Assert.True(Assert.Single(await Read(), row => row.Path == linked).Locked);
        Directory.Delete(linked, recursive: true);
        var row = Assert.Single(await Read(), row => row.Path == linked);
        Assert.True(row.Locked);
        Assert.False(row.Prunable);
    }

    // Pins: scm#2, scm#3
    [Fact]
    public async Task Locked_worktree_with_its_folder_still_says_what_would_be_lost()
    {
        await Init();
        var linked = Path.Combine(_temp, "locked");
        await Git(Root, "worktree", "add", "-b", "locked", linked);
        await Git(Root, "worktree", "lock", linked);
        await File.WriteAllTextAsync(Path.Combine(linked, "draft"), "unsaved work");
        var row = Assert.Single(await Read(), row => row.Path == linked);
        Assert.True(row.Locked);
        Assert.True(row.IsDirty);
        Assert.Equal(1, row.Unpushed);
        Assert.Equal("First subject", row.HeadSubject);
    }

    // Pins: scm#2
    [Fact]
    public async Task Missing_unlocked_worktree_is_prunable_and_does_not_fail_the_list()
    {
        await Init();
        var linked = Path.Combine(_temp, "gone");
        await Git(Root, "worktree", "add", "-b", "gone", linked);
        Directory.Delete(linked, recursive: true);
        Assert.True(Assert.Single(await Read(), row => row.Path == linked).Prunable);
    }

    // Pins: scm#8
    [Fact]
    public async Task Reading_does_not_refresh_the_index_when_only_mtime_changed()
    {
        await Init();
        var tracked = Path.Combine(Root, "tracked");
        await File.WriteAllTextAsync(tracked, "unchanged content");
        await Git(Root, "add", "tracked");
        await Commit("Tracked file");
        var index = Path.Combine(Root, ".git", "index");
        var before = await File.ReadAllBytesAsync(index);
        File.SetLastWriteTimeUtc(tracked, File.GetLastWriteTimeUtc(tracked).AddMinutes(-10));
        Assert.False(Assert.Single(await Read()).IsDirty);
        Assert.Equal(before, await File.ReadAllBytesAsync(index));
    }

    // Pins: scm#3
    [Fact]
    public async Task Head_details_match_the_commit_in_unix_milliseconds()
    {
        await Init();
        var row = Assert.Single(await Read());
        Assert.Equal("First subject", row.HeadSubject);
        Assert.Equal(long.Parse((await Git(Root, "log", "-1", "--format=%ct")).Trim()) * 1000, row.HeadCommittedAt);
    }

    // Pins: scm#1, scm#2
    [Fact]
    public async Task Bare_repository_reads_without_per_folder_commands()
    {
        Directory.CreateDirectory(Root);
        await Git(Root, "init", "--bare");
        var row = Assert.Single(await Read());
        Assert.True(row.IsBare);
        Assert.True(row.IsMain);
        Assert.Null(row.Head);
        Assert.False(row.IsDirty);
        Assert.Equal(0, row.Unpushed);
    }

    // Pins: scm#6
    [Fact]
    public async Task Non_repository_shows_gits_own_error()
    {
        Directory.CreateDirectory(Root);
        var error = await Assert.ThrowsAsync<CallFailed>(() => Read());
        Assert.Contains("not a git repository", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }
}
