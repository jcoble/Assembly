using Assembly.Infrastructure.Git;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.GitHub;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class PullRequestReaderTests
{
    // Pins: g9#20
    [Fact]
    public async Task File_context_uses_exact_revision_and_original_rename_path_without_writing_the_checkout()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Run("remote", "add", "origin", "https://github.com/owner/repo");
        var reads = new List<string>();
        var gh = new RawFakeGh(args =>
        {
            if (args[0] == "pr") return Answer(View);
            var endpoint = args[^1];
            if (endpoint.Contains("/files?")) return Answer("""[{"filename":"new name.rs","previous_filename":"old name.rs","status":"renamed","additions":1,"deletions":1}]""");
            if (endpoint.Contains("/comments?")) return Answer("[]");
            reads.Add(endpoint); return Answer(endpoint.Contains("ref=bbbb") ? "old\n" : "new λ\n");
        });
        var reader = new PullRequestReader(repo.Git, gh, repo.Root, new GitTextPatch(repo.Git));
        var result = await reader.FileAsync(repo.Root, 7, "new name.rs", new string('b', 40), new string('a', 40), default);
        Assert.False(result.Binary); Assert.Contains("-old", result.Patch); Assert.Contains("+new λ", result.Patch);
        Assert.Contains(reads, endpoint => endpoint.Contains("old%20name.rs?ref=" + new string('b', 40)));
        Assert.Contains(reads, endpoint => endpoint.Contains("new%20name.rs?ref=" + new string('a', 40)));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "a"))); Assert.False(Directory.Exists(Path.Combine(repo.Root, "b")));
        await Assert.ThrowsAsync<CallFailed>(() => reader.FileAsync(repo.Root, 7, "../outside", new string('b', 40), new string('a', 40), default));
        await Assert.ThrowsAsync<CallFailed>(() => reader.FileAsync(repo.Root, 7, "new name.rs", new string('c', 40), new string('a', 40), default));
    }
    // Pins: g9#20
    [Fact]
    public async Task Invalid_utf8_is_binary_and_missing_raw_bytes_is_an_explicit_failure()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Run("remote", "add", "origin", "https://github.com/owner/repo");
        GhRun Respond(IReadOnlyList<string> args, bool raw)
        {
            if (args[0] == "pr") return Answer(View);
            if (args[^1].Contains("/files?")) return Answer("""[{"filename":"binary","status":"added"}]""");
            if (args[^1].Contains("/comments?")) return Answer("[]");
            return new GhRun(0, "lossy replacement", "") { OutputBytes = raw ? new ReadOnlyMemory<byte>(new byte[] { 255 }) : null };
        }
        var binary = await new PullRequestReader(repo.Git, new RawFakeGh(args => Respond(args, true)), repo.Root, new GitTextPatch(repo.Git)).FileAsync(repo.Root, 7, "binary", new string('b', 40), new string('a', 40), default);
        Assert.True(binary.Binary); Assert.Empty(binary.Patch);
        var error = await Assert.ThrowsAsync<CallFailed>(() => new PullRequestReader(repo.Git, new RawFakeGh(args => Respond(args, false)), repo.Root, new GitTextPatch(repo.Git)).FileAsync(repo.Root, 7, "binary", new string('b', 40), new string('a', 40), default));
        Assert.Contains("exact file bytes", error.Message);
    }
    private static GhRun Answer(string text) => new(0, text, "") { OutputBytes = Encoding.UTF8.GetBytes(text) };
    private sealed class RawFakeGh(Func<IReadOnlyList<string>, GhRun> answer) : IGh
    {
        public Task<GhRun> RunAsync(string folder, IReadOnlyList<string> args, string? input, TimeSpan limit, CancellationToken ct) => Task.FromResult(answer(args));
    }
}
