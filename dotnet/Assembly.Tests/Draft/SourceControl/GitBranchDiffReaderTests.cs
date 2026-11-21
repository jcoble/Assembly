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
