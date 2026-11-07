using Assembly.Infrastructure.Git;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.GitHub;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class PullRequestReaderTests
{
    // Pins: g9#12, g9#19
    [Fact]
    public async Task Search_keeps_project_scope_state_terms_and_cursor_and_drops_outside_repositories()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        var gh = new FakeGh((args, input) =>
        {
            Assert.Equal(new[] { "api", "graphql", "--input", "-" }, args);
            using var query = JsonDocument.Parse(input!); var variables = query.RootElement.GetProperty("variables");
            Assert.Contains("repo:owner/repo", variables.GetProperty("query").GetString()); Assert.Contains("review-requested:@me", variables.GetProperty("query").GetString());
            Assert.Contains("is:open", variables.GetProperty("query").GetString()); Assert.Contains("head:feature", variables.GetProperty("query").GetString()); Assert.Equal("cursor1", variables.GetProperty("after").GetString());
            return """{"data":{"search":{"issueCount":2,"pageInfo":{"hasNextPage":true,"endCursor":"cursor2"},"nodes":[{"number":1,"title":"Known","url":"https://github.com/owner/repo/pull/1","repository":{"nameWithOwner":"owner/repo"}},{"number":2,"title":"Outside","url":"https://github.com/other/repo/pull/2","repository":{"nameWithOwner":"other/repo"}}]}}}""";
        });
        var page = await new PullRequestReader(repo.Git, gh, repo.Root, new GitTextPatch(repo.Git)).ListAsync(["owner/repo"], "open", "review-requested:@me head:feature", "cursor1", default);
        Assert.Equal("Known", Assert.Single(page.Items).Title); Assert.Equal("cursor2", page.Cursor); Assert.Equal(2, page.Total);
    }
    // Pins: g9#20
    [Fact]
    public async Task Detail_reads_files_inline_comments_checks_and_orders_actual_timeline_events()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Run("remote", "add", "origin", "https://github.com/owner/repo.git");
        var gh = new FakeGh((args, _) => args[0] == "pr" ? View : args[1] == "graphql"
            ? """{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[{"id":"T1","isResolved":true,"isOutdated":false,"path":"new name.rs","line":1,"originalLine":1,"diffSide":"RIGHT","comments":{"nodes":[{"databaseId":4,"author":{"login":"reviewer"},"body":"Line comment","createdAt":"2026-11-03T11:00:00Z"}]}}]}}}}}"""
            : """[{"filename":"new name.rs","previous_filename":"old.rs","status":"renamed","additions":1,"deletions":1,"patch":"@@ -1 +1 @@\n-a\n+b"}]""");
        var detail = await new PullRequestReader(repo.Git, gh, repo.Root, new GitTextPatch(repo.Git)).DetailAsync(repo.Root, 7, default);
        Assert.Equal("owner/repo", detail.Repository); Assert.Equal("old.rs", Assert.Single(detail.Files).PreviousPath);
        Assert.Contains(detail.Comments, comment => comment.Id == "4" && comment.Path == "new name.rs" && comment.Line == 1);
        Assert.Equal("build", Assert.Single(detail.Checks).Name);
        var thread = Assert.Single(detail.Threads); Assert.True(thread.Resolved); Assert.Equal(["4"], thread.CommentIds);
        Assert.Equal("Description", detail.Body); Assert.NotEmpty(detail.BodyBlocks);
        Assert.All(detail.Comments, comment => Assert.NotEmpty(comment.BodyBlocks));
        Assert.All(detail.Reviews, review => Assert.NotEmpty(review.BodyBlocks));
        Assert.Equal(new[] { "commit", "comment", "comment", "review" }, detail.Timeline.Select(item => item.Kind));
    }
    // Pins: g9#20
    [Fact]
    public async Task A_wrong_number_or_non_github_origin_is_refused()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Run("remote", "add", "origin", "https://gitlab.com/owner/repo");
        var gh = new FakeGh((_, _) => throw new InvalidOperationException("Must not call GitHub"));
        await Assert.ThrowsAsync<CallFailed>(() => new PullRequestReader(repo.Git, gh, repo.Root, new GitTextPatch(repo.Git)).DetailAsync(repo.Root, 7, default));
        await repo.Run("remote", "set-url", "origin", "https://github.com/owner/repo");
        await Assert.ThrowsAsync<CallFailed>(() => new PullRequestReader(repo.Git, new FakeGh((_, _) => View), repo.Root, new GitTextPatch(repo.Git)).DetailAsync(repo.Root, 8, default));
    }
    [Fact]
    public async Task Session_queue_resolves_actual_checkout_origin_for_its_search_scope()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync();
        await repo.Run("remote", "add", "origin", "git@github.com:actual/checkout.git");
        var gh = new FakeGh((args, input) =>
        {
            using var request = JsonDocument.Parse(input!);
            Assert.Contains("repo:actual/checkout", request.RootElement.GetProperty("variables").GetProperty("query").GetString());
            return """{"data":{"search":{"issueCount":0,"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}""";
        });
        var page = await new PullRequestReader(repo.Git, gh, repo.Root, new GitTextPatch(repo.Git)).ListForRootAsync(repo.Root, "open", null, null, default);
        Assert.Empty(page.Items);
    }
    private const string View = """
    {"number":7,"title":"PR","body":"Description","url":"https://github.com/owner/repo/pull/7","author":{"login":"author"},"headRefName":"feature","baseRefName":"main","headRefOid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseRefOid":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN","isDraft":false,"createdAt":"2026-11-03T09:00:00Z","commits":[{"oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","messageHeadline":"Commit","authors":[{"login":"author"}],"committedDate":"2026-11-03T09:30:00Z"}],"comments":[{"id":"C1","author":{"login":"author"},"body":"Discussion","createdAt":"2026-11-03T10:00:00Z"}],"reviews":[{"author":{"login":"reviewer"},"body":"Review","state":"COMMENTED","submittedAt":"2026-11-03T12:00:00Z"}],"reviewRequests":[],"statusCheckRollup":[{"name":"build","status":"COMPLETED","conclusion":"SUCCESS","detailsUrl":"https://github.com/owner/repo/actions/runs/1"}]}
    """;
    private sealed class FakeGh(Func<IReadOnlyList<string>, string?, string> answer) : IGh
    {
        public Task<GhRun> RunAsync(string folder, IReadOnlyList<string> args, string? input, TimeSpan limit, CancellationToken ct) => Task.FromResult(new GhRun(0, answer(args, input), ""));
    }
}
