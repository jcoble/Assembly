using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.Worktrees;

public sealed class WorktreeReaderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "assembly-worktrees-" + Guid.NewGuid());
    private readonly GitCli _git = new();
    private string Root => Path.Combine(_temp, "main");
    private async Task<string> Git(string folder, params string[] arguments)
    {
        var records = new List<string>();
        await foreach (var record in _git.ReadAsync(folder, arguments, null, CancellationToken.None))
            records.Add(record);
        return string.Join('\0', records);
    }
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

    // Pins: scm#2
    [Fact]
    public async Task Untracked_files_count_as_dirty_and_clean_worktrees_do_not()
    {
        await Init();
        Assert.False(Assert.Single(await Read()).IsDirty);
        await File.WriteAllTextAsync(Path.Combine(Root, "untracked"), "new content");
        Assert.True(Assert.Single(await Read()).IsDirty);
        await Git(Root, "config", "status.showUntrackedFiles", "no");
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
    public async Task Missing_unlocked_worktree_is_prunable_and_still_says_what_would_be_lost()
    {
        await Init();
        var linked = Path.Combine(_temp, "gone");
        await Git(Root, "worktree", "add", "-b", "gone", linked);
        Directory.Delete(linked, recursive: true);
        var row = Assert.Single(await Read(), row => row.Path == linked);
        Assert.True(row.Prunable);
        Assert.Equal(1, row.Unpushed);
        Assert.Equal("First subject", row.HeadSubject);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }
}
