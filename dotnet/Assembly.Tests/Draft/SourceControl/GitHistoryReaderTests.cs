using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitHistoryReaderTests
{
    // Pins: g9#15
    [Fact]
    public async Task Pages_of_24_continue_by_cursor()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        for (var n = 0; n < 25; n++) await repo.Commit($"Commit {n}");
        var reader = new GitHistoryReader(repo.Git);
        var first = await reader.ReadAsync(repo.Root, null, null, default);
        var second = await reader.ReadAsync(repo.Root, first.NextCursor, null, default);
        Assert.Equal(24, first.Commits.Count);
        Assert.Equal("24", first.NextCursor);
        Assert.Equal("Commit 24", first.Commits[0].Subject);
        Assert.Equal("Commit 0", Assert.Single(second.Commits).Subject);
        Assert.Null(second.NextCursor);
        Assert.Equal(25, first.Commits.Concat(second.Commits).Select(commit => commit.Sha).Distinct().Count());
    }

    // Pins: g9#11, g9#15
    [Fact]
    public async Task A_repository_with_no_commits_is_an_empty_page()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        var page = await new GitHistoryReader(repo.Git).ReadAsync(repo.Root, null, null, default);
        Assert.Empty(page.Commits); Assert.Null(page.NextCursor);
    }

    // Pins: g9#18
    [Theory]
    [InlineData("-leading.txt")]
    [InlineData(":(top)literal[1].txt")]
    public async Task File_history_is_literal_and_follows_a_rename(string name)
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        await repo.Write("original.txt", "retained content\n"); await repo.Commit("Original");
        await repo.Run("mv", "--", "original.txt", name); await repo.Commit("Rename");
        await repo.Write("unrelated.txt", "unrelated\n"); await repo.Commit("Unrelated");
        var page = await new GitHistoryReader(repo.Git).ReadAsync(repo.Root, null, name, default);
        Assert.Equal(new[] { "Rename", "Original" }, page.Commits.Select(commit => commit.Subject));
    }

    // Pins: g9#11, g9#15, g9#17
    [Fact]
    public async Task A_detached_head_preserves_commit_metadata_and_root_parent_identity()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        await repo.Commit("Subject\n\nFull body with Unicode λ");
        var head = await repo.Head(); await repo.Run("checkout", "--detach", head);
        var commit = Assert.Single((await new GitHistoryReader(repo.Git).ReadAsync(repo.Root, null, null, default)).Commits);
        Assert.Equal(head, commit.Sha); Assert.StartsWith(commit.ShortSha, head);
        Assert.Equal("Subject", commit.Subject); Assert.Contains("Full body with Unicode λ", commit.Body);
        Assert.Equal("History Test", commit.Author); Assert.Empty(commit.Parents);
        Assert.Contains(commit.Refs, value => value.Contains("HEAD", StringComparison.Ordinal));
        Assert.True(DateTimeOffset.TryParse(commit.CommittedAt, out _));
    }

    // Pins: g9#15
    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-cursor")]
    [InlineData("2147483648")]
    public async Task Invalid_cursors_are_refused(string cursor)
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        await Assert.ThrowsAsync<CallFailed>(() => new GitHistoryReader(repo.Git).ReadAsync(repo.Root, cursor, null, default));
    }
}
