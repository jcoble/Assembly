using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitCommitReaderTests
{
    // Pins: g9#17
    [Fact]
    public async Task Merge_commit_files_are_against_the_first_parent()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        await repo.Write("base.txt", "base\n"); await repo.Commit("Root");
        await repo.Run("checkout", "-b", "feature"); await repo.Write("feature.txt", "feature\n"); await repo.Commit("Feature");
        await repo.Run("checkout", "main"); await repo.Write("main.txt", "main\n"); await repo.Commit("Main");
        await repo.Run("-c", "commit.gpgsign=false", "merge", "--no-ff", "feature", "-m", "Merge fixture");
        var reader = new GitCommitReader(repo.Git);
        var files = await reader.FilesAsync(repo.Root, await repo.Head(), default);
        Assert.Equal("feature.txt", Assert.Single(files).Path);
        var diff = await reader.DiffAsync(repo.Root, await repo.Head(), "feature.txt", default);
        Assert.Contains("+feature", diff.Unstaged); Assert.Empty(diff.Staged);
    }

    // Pins: g9#17, g9#18
    [Fact]
    public async Task Renamed_paths_retain_both_names_and_literal_commit_diff()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        await repo.Write("original.txt", "one\ntwo\nthree\n"); await repo.Commit("Root");
        await repo.Run("mv", "--", "original.txt", "-renamed[1].txt"); await repo.Commit("Rename");
        var reader = new GitCommitReader(repo.Git);
        var file = Assert.Single(await reader.FilesAsync(repo.Root, await repo.Head(), default));
        Assert.Equal('R', file.Letter); Assert.Equal("original.txt", file.OriginalPath); Assert.Equal("-renamed[1].txt", file.Path);
        var diff = await reader.DiffAsync(repo.Root, await repo.Head(), file.Path, default);
        Assert.Equal(file.Path, diff.Path); Assert.False(diff.Binary);
    }

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
