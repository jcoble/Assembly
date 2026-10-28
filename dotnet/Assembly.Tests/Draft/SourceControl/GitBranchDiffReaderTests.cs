using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

/// <summary>Reads a branch's changes from its merge base in temporary clones without changing the index.</summary>
public sealed class GitBranchDiffReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "assembly-branch-diff-" + Guid.NewGuid());
    private string Root => Path.Combine(_folder, "clone");
    private string Origin => Path.Combine(_folder, "origin");
    private readonly GitCli _git = new();

    private async Task Git(string root, params string[] arguments)
    {
        await foreach (var _ in _git.ReadAsync(root, arguments, null, CancellationToken.None)) { }
    }

    private Task<GitBranchDiff> Read() => new GitBranchDiffReader(_git).ReadAsync(Root, CancellationToken.None);
    private Task Commit(string root) => Git(root, "-c", "commit.gpgsign=false", "commit", "-am", "Branch fixture");

    private async Task Init()
    {
        Directory.CreateDirectory(Origin);
        await Git(Origin, "init", "-b", "main");
        await Git(Origin, "config", "user.name", "Branch Test");
        await Git(Origin, "config", "user.email", "branch@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(Origin, "tracked"), "old\n");
        await File.WriteAllBytesAsync(Path.Combine(Origin, "binary"), [1, 0, 2]);
        await Git(Origin, "add", ".");
        await Commit(Origin);
        await Git(_folder, "clone", Origin, Root);
        await Git(Root, "config", "user.name", "Branch Test");
        await Git(Root, "config", "user.email", "branch@example.invalid");
        await Git(Root, "checkout", "-b", "feature");
    }

    // Pins: scm#18
    [Fact]
    public async Task Committed_and_working_changes_are_both_compared_with_the_base()
    {
        await Init();
        await File.AppendAllTextAsync(Path.Combine(Root, "tracked"), "committed\n");
        await Commit(Root);
        await File.AppendAllTextAsync(Path.Combine(Root, "tracked"), "working\n");
        var diff = await Read();
        Assert.Equal("origin/main", diff.Base);
        Assert.Contains("+committed\n+working\n", diff.Diff);
        Assert.False(diff.Truncated);
    }

    // Pins: scm#18
    [Fact]
    public async Task Later_changes_on_main_are_not_part_of_the_branch_diff()
    {
        await Init();
        await File.AppendAllTextAsync(Path.Combine(Root, "tracked"), "branch change\n");
        await Commit(Root);
        await File.WriteAllTextAsync(Path.Combine(Origin, "main-only"), "only on main\n");
        await Git(Origin, "add", "main-only");
        await Commit(Origin);
        await Git(Root, "fetch", "origin");
        var diff = await Read();
        Assert.Contains("+branch change\n", diff.Diff);
        Assert.DoesNotContain("main-only", diff.Diff);
        Assert.DoesNotContain("only on main", diff.Diff);
    }

    // Pins: scm#19
    [Fact]
    public async Task Untracked_files_are_not_in_the_diff()
    {
        await Init();
        await File.WriteAllTextAsync(Path.Combine(Root, "untracked"), "not included\n");
        Assert.Empty((await Read()).Diff);
    }

    // Pins: scm#19
    [Fact]
    public async Task A_repository_without_origin_head_explains_the_missing_default_branch()
    {
        Directory.CreateDirectory(Root);
        await Git(Root, "init", "-b", "main");
        var error = await Assert.ThrowsAsync<CallFailed>(Read);
        Assert.Equal("this folder's repository has no origin/HEAD, so there's no default branch to compare with", error.Message);
    }

    // Pins: scm#20
    [Fact]
    public async Task Committed_binary_changes_keep_gits_binary_notice()
    {
        await Init();
        await File.WriteAllBytesAsync(Path.Combine(Root, "binary"), [1, 0, 3]);
        await Commit(Root);
        Assert.Contains("Binary files a/binary and b/binary differ", (await Read()).Diff);
    }

    // Pins: scm#20
    [Fact]
    public async Task A_long_diff_is_cut_at_the_cap()
    {
        await Init();
        await File.WriteAllTextAsync(Path.Combine(Root, "tracked"), string.Concat(Enumerable.Repeat("a long added line\n", 70000)));
        var diff = await Read();
        Assert.True(diff.Truncated);
        Assert.InRange(diff.Diff.Length, 1, GitDiffReader.Cap);
        Assert.False(char.IsHighSurrogate(diff.Diff[^1]));
    }

    // Pins: scm#20
    [Fact]
    public async Task Reading_leaves_the_index_unchanged()
    {
        await Init();
        await File.AppendAllTextAsync(Path.Combine(Root, "tracked"), "staged\n");
        await Git(Root, "add", "tracked");
        await File.AppendAllTextAsync(Path.Combine(Root, "tracked"), "working\n");
        var index = Path.Combine(Root, ".git", "index");
        var before = await File.ReadAllBytesAsync(index);
        await Read();
        Assert.Equal(before, await File.ReadAllBytesAsync(index));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
