using System.Runtime.CompilerServices;
using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Chat.SourceControl;
namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitHistoryReaderBudgetTests
{
    // Pins: g9#15, g9#17
    [Fact]
    public async Task A_large_utf8_body_is_bounded_without_losing_metadata_after_split_records()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        var message = "Subject\n\n" + string.Concat(Enumerable.Repeat("λ😀", 400_000));
        var run = await repo.Git.RunAsync(repo.Root, ["commit", "--allow-empty", "-F", "-"], message, null, default);
        Assert.Equal(0, run.ExitCode);
        var commit = Assert.Single((await new GitHistoryReader(repo.Git).ReadAsync(repo.Root, null, null, default)).Commits);
        Assert.True(Encoding.UTF8.GetByteCount(commit.Body) <= 8192); Assert.EndsWith("…", commit.Body);
        Assert.Equal("History Test", commit.Author); Assert.Empty(commit.Parents);
    }
    // Pins: g9#15
    [Fact]
    public async Task The_page_budget_counts_serialized_escaping_not_only_raw_metadata()
    {
        await Assert.ThrowsAsync<CallFailed>(() => new GitHistoryReader(new EscapedMetadata()).ReadAsync("unused", null, null, default));
    }
    private sealed class EscapedMetadata : IGit
    {
        public Task<GitRun> RunAsync(string folder, IReadOnlyList<string> args, string? input, TimeSpan? limit, CancellationToken ct) => throw new NotSupportedException();
        public async IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> args, IReadOnlyList<string>? input, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return args[0] == "rev-parse" ? new string('a', 40) : "aaaaaaa\u001f" + new string('a', 40) + "\u001f" + new string('\\', 600_000) + "\u001f\u001fAuthor\u001f2026-11-03T12:00:00Z\u001f\u001f";
        }
    }
}
