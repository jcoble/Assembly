using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitWriterMutationTests
{
    // Pins: g9#1, g9#2
    [Theory]
    [InlineData("a b.txt")]
    [InlineData("ü.txt")]
    [InlineData("-x.txt")]
    [InlineData(":(top)y")]
    public async Task Stage_and_unstage_keep_literal_paths_before_and_after_first_commit(string path)
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), "new\n");
        var writer = new GitWriter(repo.Git);
        var staged = await writer.StageAsync(repo.Root, [path], default);
        Assert.Equal('A', Assert.Single(staged.Status.Files).Index);
        var unstaged = await writer.UnstageAsync(repo.Root, [path], default);
        Assert.Equal('?', Assert.Single(unstaged.Status.Files).Index);
        await writer.StageAsync(repo.Root, [path], default); await repo.Commit("Root");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), "changed\n");
        Assert.Equal('M', Assert.Single((await writer.StageAsync(repo.Root, [path], default)).Status.Files).Index);
        Assert.Equal('M', Assert.Single((await writer.UnstageAsync(repo.Root, [path], default)).Status.Files).Worktree);
    }

    // Pins: g9#2
    [Fact]
    public async Task Discard_restores_a_rename_and_removes_only_listed_untracked_files()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        await repo.Write("a", "original\n"); await repo.Write(".gitignore", ".env\n"); await repo.Commit("Root");
        await repo.Run("mv", "a", "b"); await File.WriteAllTextAsync(Path.Combine(repo.Root, "b"), "edited\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "remove"), "remove");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "keep"), "keep");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".env"), "ignored");
        var expected = (await new GitStatusReader(repo.Git).ReadAsync(repo.Root, default)).Files;
        await new GitWriter(repo.Git).DiscardAsync(repo.Root, ["b"], ["remove"], expected, default);
        Assert.Equal("original\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "a")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "b"))); Assert.False(File.Exists(Path.Combine(repo.Root, "remove")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "keep"))); Assert.True(File.Exists(Path.Combine(repo.Root, ".env")));
    }

    // Pins: g9#2
    [Fact]
    public async Task Stale_discard_does_not_mutate_any_listed_file()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        await repo.Write("a", "original\n"); await repo.Commit("Root");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "a"), "edited\n");
        var reader = new GitStatusReader(repo.Git); var expected = (await reader.ReadAsync(repo.Root, default)).Files;
        await repo.Run("add", "a");
        var failure = await Assert.ThrowsAsync<CallFailed>(() => new GitWriter(repo.Git).DiscardAsync(repo.Root, ["a"], [], expected, default));
        Assert.Contains("Files changed since you looked", failure.Message);
        Assert.Equal("edited\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "a")));
        Assert.Equal('M', Assert.Single((await reader.ReadAsync(repo.Root, default)).Files).Index);
    }

    // Pins: g9#1, g9#2
    [Fact]
    public async Task All_operations_cover_paths_beyond_the_display_cap_and_refuse_stale_fingerprints()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        for (var n = 0; n < 1002; n++) await File.WriteAllTextAsync(Path.Combine(repo.Root, $"{n:D4}.txt"), "original\n");
        await repo.Run("add", "."); await repo.Commit("Root");
        for (var n = 0; n < 1002; n++) await File.WriteAllTextAsync(Path.Combine(repo.Root, $"{n:D4}.txt"), "edited\n");
        var reader = new GitStatusReader(repo.Git); var before = await reader.ReadAsync(repo.Root, default); var writer = new GitWriter(repo.Git);
        Assert.Equal(1000, before.Files.Count); Assert.Equal(1002, before.TotalFiles);
        var staged = await writer.StageAllAsync(repo.Root, before.Fingerprint, default);
        Assert.All((await reader.ReadFullAsync(repo.Root, default)).All, file => Assert.Equal('M', file.Index));
        await Assert.ThrowsAsync<CallFailed>(() => writer.DiscardAllAsync(repo.Root, before.Fingerprint, true, default));
        await writer.DiscardAllAsync(repo.Root, staged.Status.Fingerprint, true, default);
        Assert.Empty((await reader.ReadAsync(repo.Root, default)).Files);
        Assert.Equal("original\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "1001.txt")));
    }

    // Pins: g9#1, g9#6
    [Fact]
    public async Task Commit_preserves_multiline_stdin_and_stale_head_refuses_before_mutation()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        await repo.Write("a", "a\n"); var writer = new GitWriter(repo.Git);
        await writer.CommitAsync(repo.Root, "Subject\n\nBody line\nSecond line", false, null, default);
        var head = await repo.Head();
        var commit = Assert.Single((await new GitHistoryReader(repo.Git).ReadAsync(repo.Root, null, null, default)).Commits);
        Assert.Equal("Subject", commit.Subject); Assert.Contains("Body line\nSecond line", commit.Body);
        await repo.Commit("Newer"); await repo.Write("b", "b\n");
        await Assert.ThrowsAsync<CallFailed>(() => writer.CommitAsync(repo.Root, "Stale", false, head, default));
        Assert.Equal("Newer", (await new GitHistoryReader(repo.Git).ReadAsync(repo.Root, null, null, default)).Commits[0].Subject);
        await writer.CommitAsync(repo.Root, "", true, await repo.Head(), default);
        Assert.Equal("Newer", (await new GitHistoryReader(repo.Git).ReadAsync(repo.Root, null, null, default)).Commits[0].Subject);
    }
    // Pins: g9#9
    [Fact]
    public async Task A_plain_folder_status_says_it_is_not_a_repository()
    {
        using var repo = new GitHistoryTestRepository(); Directory.CreateDirectory(repo.Root);
        var failure = await Assert.ThrowsAsync<CallFailed>(() => new GitStatusReader(repo.Git).ReadAsync(repo.Root, default));
        Assert.StartsWith("This folder isn't a git repository yet.", failure.Message);
        Assert.Contains("not a git repository", failure.Message);
    }

}
