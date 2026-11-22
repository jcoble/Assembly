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
}
