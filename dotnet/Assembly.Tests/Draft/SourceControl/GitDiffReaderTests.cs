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

    // Pins: scm#14
    [Fact]
    public async Task Staged_then_edited_has_separate_diffs()
    {
        await Init();
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked"), "staged\n");
        await Git("add", "tracked");
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked"), "working\n");
        var diff = await Read();
        Assert.Contains("-old\n", diff.Staged);
        Assert.Contains("+staged\n", diff.Staged);
        Assert.Contains("-staged\n", diff.Unstaged);
        Assert.Contains("+working\n", diff.Unstaged);
    }

    // Pins: scm#14
    [Fact]
    public async Task Staged_rename_keeps_its_old_path()
    {
        await Init();
        await Git("mv", "tracked", "renamed");
        await File.AppendAllTextAsync(Path.Combine(_root, "renamed"), "added\n");
        await Git("add", "renamed");
        var diff = await Read("renamed", "tracked");
        Assert.Contains("rename from tracked\n", diff.Staged);
        Assert.Contains("rename to renamed\n", diff.Staged);
        Assert.Contains("+added\n", diff.Staged);
    }

    // Pins: scm#14
    [Fact]
    public async Task Untracked_file_shows_every_line_added()
    {
        await Init();
        await File.WriteAllTextAsync(Path.Combine(_root, "new file"), "first\nsecond\n");
        var diff = await Read("new file");
        Assert.Empty(diff.Staged);
        Assert.Contains("+first\n+second\n", diff.Unstaged);
    }

    // Pins: scm#14
    [Fact]
    public async Task Deleted_file_shows_its_removed_lines()
    {
        await Init();
        File.Delete(Path.Combine(_root, "tracked"));
        Assert.Contains("-old\n-keep one\n-keep two\n-keep three\n", (await Read()).Unstaged);
    }

    // Pins: scm#16
    [Fact]
    public async Task Binary_change_has_no_diff_text()
    {
        await Init();
        await File.WriteAllBytesAsync(Path.Combine(_root, "tracked"), [1, 0, 2]);
        await Git("add", "tracked");
        await Commit();
        await File.WriteAllBytesAsync(Path.Combine(_root, "tracked"), [1, 0, 3]);
        var diff = await Read();
        Assert.True(diff.Binary);
        Assert.Empty(diff.Staged);
        Assert.Empty(diff.Unstaged);
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

    // Pins: scm#14, scm#16
    [Fact]
    public async Task A_binary_working_file_keeps_its_staged_text_diff()
    {
        await Init();
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked"), "staged\n");
        await Git("add", "tracked");
        await File.WriteAllBytesAsync(Path.Combine(_root, "tracked"), [1, 0, 2]);
        var diff = await Read();
        Assert.True(diff.Binary);
        Assert.Contains("+staged\n", diff.Staged);
        Assert.Empty(diff.Unstaged);
    }

    // Pins: scm#14
    [Fact]
    public async Task An_untracked_name_with_pattern_characters_is_read_as_written()
    {
        await Init();
        await File.WriteAllTextAsync(Path.Combine(_root, "a1.txt"), "tracked\n");
        await Git("add", "a1.txt");
        await Commit();
        await File.WriteAllTextAsync(Path.Combine(_root, "a[1].txt"), "hello\n");
        Assert.Contains("+hello\n", (await Read("a[1].txt")).Unstaged);
    }

    // Pins: scm#14
    [Fact]
    public async Task A_staged_deletion_is_only_in_the_staged_part()
    {
        await Init();
        await Git("rm", "-q", "tracked");
        var diff = await Read();
        Assert.Contains("-old\n", diff.Staged);
        Assert.Empty(diff.Unstaged);
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
