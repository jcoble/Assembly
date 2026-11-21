using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

/// <summary>Reads one file's diffs in temporary repositories without changing the index.</summary>
public sealed class GitDiffReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assembly-diff-" + Guid.NewGuid());
    private readonly GitCli _git = new();
    private async Task Git(params string[] arguments)
    {
        await foreach (var _ in _git.ReadAsync(_root, arguments, null, CancellationToken.None)) { }
    }
    private Task<GitFileDiff> Read(string path = "tracked", string? originalPath = null) =>
        new GitDiffReader(_git).ReadAsync(_root, path, originalPath, CancellationToken.None);

    private async Task Init()
    {
        Directory.CreateDirectory(_root);
        await Git("init", "-b", "main");
        await Git("config", "user.name", "Diff Test");
        await Git("config", "user.email", "diff@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked"), "old\nkeep one\nkeep two\nkeep three\n");
        await Git("add", "tracked");
        await Commit();
    }
    private Task Commit() => Git("-c", "commit.gpgsign=false", "commit", "-m", "Diff fixture");

    // Pins: scm#14
    [Fact]
    public async Task Unstaged_edit_has_only_working_tree_changes()
    {
        await Init();
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked"), "new\n");
        var diff = await Read();
        Assert.Equal("tracked", diff.Path);
        Assert.Empty(diff.Staged);
        Assert.Contains("-old\n", diff.Unstaged);
        Assert.Contains("+new\n", diff.Unstaged);
        Assert.False(diff.Truncated);
    }

    // Pins: scm#16
    [Fact]
    public async Task A_long_diff_stops_at_the_cap_across_both_parts()
    {
        await Init();
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked"), string.Concat(Enumerable.Repeat("a staged line\n", 30000)));
        await Git("add", "tracked");
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked"), string.Concat(Enumerable.Repeat("a working line\n", 30000)));
        var diff = await Read();
        Assert.True(diff.Truncated);
        Assert.Equal(GitDiffReader.Cap, diff.Staged.Length + diff.Unstaged.Length);
        Assert.NotEmpty(diff.Unstaged);
    }

    // Pins: scm#17
    [Fact]
    public async Task Reading_leaves_the_index_unchanged()
    {
        await Init();
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked"), "edited\n");
        var index = Path.Combine(_root, ".git", "index");
        var before = await File.ReadAllBytesAsync(index);
        await Read();
        Assert.Equal(before, await File.ReadAllBytesAsync(index));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
