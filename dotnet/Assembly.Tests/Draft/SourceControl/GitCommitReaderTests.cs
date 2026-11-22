using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitCommitReaderTests
{
    // Pins: g9#17
    [Theory]
    [InlineData("HEAD")]
    [InlineData("--help")]
    [InlineData("abcdef")]
    [InlineData("1234567;echo unsafe")]
    public async Task A_bad_sha_is_refused_before_git(string sha)
    {
        using var repo = new GitHistoryTestRepository();
        var reader = new GitCommitReader(repo.Git);
        await Assert.ThrowsAsync<CallFailed>(() => reader.FilesAsync(repo.Root, sha, default));
        await Assert.ThrowsAsync<CallFailed>(() => reader.DiffAsync(repo.Root, sha, "file", default));
    }
}
