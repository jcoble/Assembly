using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Common.GitHub;
using Assembly.Application.Features.Chat.Rows.Markdown;
namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

/// <summary>Git supplies origin identity; GitHub supplies all hosted PR state.</summary>
public sealed partial class PullRequestReader(IGit git, IGh gh, string applicationFolder, IGitTextPatch patches)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);
    private const int DetailBytes = 3_000_000;
    private const string ViewFields = "number,title,body,url,author,headRefName,baseRefName,headRefOid,baseRefOid,state,isDraft,mergeable,mergeStateStatus,reviewDecision,createdAt,mergedAt,mergedBy,closedAt,commits,comments,reviews,reviewRequests,statusCheckRollup";
    private const string SearchQuery = "query($query:String!,$first:Int!,$after:String){search(query:$query,type:ISSUE_ADVANCED,first:$first,after:$after){issueCount pageInfo{hasNextPage endCursor} nodes{... on PullRequest{number title url isDraft updatedAt state headRefName baseRefName repository{nameWithOwner} author{login} reviewDecision commits(last:1){nodes{commit{statusCheckRollup{state}}}}}}}}";
    public async Task<PullRequestPage> ListForRootAsync(string root, string state, string? search, string? cursor, CancellationToken ct) =>
        await ListAsync([await Repository(root, ct)], state, search, cursor, ct);
    public async Task<PullRequestPage> ListAsync(IReadOnlyList<string> repositories, string state, string? search, string? cursor, CancellationToken ct)
    {
        if (repositories.Count > 32) throw new CallBadArguments("Choose at most 32 repositories.");
        var scopes = repositories.Select(GitHubRepository.Parse).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (scopes.Length == 0) return new([], null, 0);
        // The list's filters arrive as search terms; the state says only whether closed and merged pull requests are left out.
        var terms = state switch { "open" => " is:open", "all" => "", _ => throw new CallBadArguments("Choose open or all pull requests.") };
        var query = "is:pr " + (scopes.Length > 1 ? "(" + string.Join(" OR ", scopes.Select(repo => "repo:" + repo)) + ")" : "repo:" + scopes[0]) + terms;
        if (!string.IsNullOrWhiteSpace(search)) query += " " + search.Trim();
        var request = new JsonObject { ["query"] = SearchQuery, ["variables"] = new JsonObject { ["query"] = query, ["first"] = 30, ["after"] = cursor } };
        var result = await Json(applicationFolder, ["api", "graphql", "--input", "-"], request.ToJsonString(), ct);
        if (result.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw new CallFailed("GitHub search failed: " + Text(errors[0], "message"));
        var found = Required(result, "data", "search");
        var allowed = scopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<PullRequestSummary> items = [];
        foreach (var row in Array(found, "nodes"))
        {
            var repository = Text(row, "repository", "nameWithOwner");
            if (!allowed.Contains(repository)) continue;
            items.Add(new(repository, null, Number(row, "number"), Text(row, "title"), Text(row, "url"), Bool(row, "isDraft"), Text(row, "updatedAt"), Text(row, "state"), Text(row, "author", "login"), Text(row, "headRefName"), Text(row, "baseRefName"))
            { Checks = RollupWord(row), ReviewDecision = Text(row, "reviewDecision") });
        }
        var info = Required(found, "pageInfo");
        return new(items, Bool(info, "hasNextPage") ? Text(info, "endCursor") : null, Number(found, "issueCount"));
    }
    public Task<PullRequestDetail> DetailAsync(string root, int number, CancellationToken ct) => DetailCore(root, null, number, ct);
    public Task<PullRequestDetail> ProjectDetailAsync(string root, string repository, int number, CancellationToken ct) =>
        DetailCore(root, GitHubRepository.Parse(repository), number, ct);
    private async Task<PullRequestDetail> DetailCore(string root, string? expectedRepository, int number, CancellationToken ct)
    {
        if (number <= 0) throw new CallBadArguments("Pull request number must be positive.");
        var repository = await Repository(root, ct);
        if (expectedRepository is not null && !string.Equals(repository, expectedRepository, StringComparison.OrdinalIgnoreCase))
            throw new CallFailed("The project repository changed. Refresh and try again.", "source_changed");
        var view = await Json(root, ["pr", "view", number.ToString(CultureInfo.InvariantCulture), "-R", repository, "--json", ViewFields], null, ct);
        if (Number(view, "number") != number) throw new CallFailed("GitHub returned the wrong pull request.");
        var changed = await Json(root, ["api", $"repos/{repository}/pulls/{number}/files?per_page=100&page=1"], null, ct);
        var (inline, threads) = await Threads(root, repository, number, ct);
        if (changed.ValueKind != JsonValueKind.Array) throw new CallFailed("GitHub returned invalid pull request files.");
        var files = changed.EnumerateArray().Select(row => new PullRequestFile(Text(row, "filename"), Optional(row, "previous_filename"), Text(row, "status"), Number(row, "additions"), Number(row, "deletions"), Optional(row, "patch"))).ToArray();
        var comments = Array(view, "comments").Select(row => new PullRequestComment(Text(row, "id"), Text(row, "author", "login"), GitHubText.Tidy(Text(row, "body")), Text(row, "createdAt"), null, null, null, null) { BodyBlocks = SafeMarkdown.Parse(GitHubText.Tidy(Text(row, "body"))) }).Concat(inline).ToArray();
        var reviews = Array(view, "reviews").Select(row => new PullRequestReview(Text(row, "author", "login"), GitHubText.Tidy(Text(row, "body")), Text(row, "state"), Text(row, "submittedAt")) { BodyBlocks = SafeMarkdown.Parse(GitHubText.Tidy(Text(row, "body"))) }).ToArray();
        var commits = Array(view, "commits").Select(row => new PullRequestCommit(Text(row, "oid"), Text(row, "messageHeadline"), Array(row, "authors").Select(author => Text(author, "login")).FirstOrDefault() ?? "", Text(row, "committedDate"))).ToArray();
        var reviewers = Array(view, "reviewRequests").Select(row => Optional(row, "login") ?? Text(row, "name")).ToArray();
        var checks = Array(view, "statusCheckRollup").Select(Check).ToArray();
        var timeline = comments.Select(comment => new PullRequestTimeline("comment", comment.Author, comment.Body, comment.CreatedAt, CommentId: comment.Id))
            .Concat(reviews.Select(review => new PullRequestTimeline("review", review.Author, review.Body, review.SubmittedAt)))
            .Concat(commits.Select(commit => new PullRequestTimeline("commit", commit.Author, commit.Headline, commit.CommittedAt, commit.Oid)))
            .OrderBy(item => DateTimeOffset.TryParse(item.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTimeOffset.MinValue).ToArray();
        var detail = new PullRequestDetail(repository, root, number, Text(view, "title"), Text(view, "body"), Text(view, "url"), Text(view, "author", "login"), Text(view, "headRefName"), Text(view, "baseRefName"), Text(view, "headRefOid"), Text(view, "baseRefOid"), Text(view, "state"), Bool(view, "isDraft"), Text(view, "mergeable"), Text(view, "reviewDecision"), Text(view, "createdAt"), Text(view, "mergedAt"), Text(view, "mergedBy", "login"), Text(view, "closedAt"), commits, comments, reviews, reviewers, checks, files, files.Length == 100, timeline) { BodyBlocks = SafeMarkdown.Parse(GitHubText.Tidy(Text(view, "body"))), MergeState = Text(view, "mergeStateStatus"), Threads = threads };
        if (Size(detail) <= DetailBytes) return detail;
        foreach (var index in Enumerable.Range(0, files.Length).OrderByDescending(index => files[index].Patch?.Length ?? 0))
        {
            if (files[index].Patch is null) continue;
            files[index] = files[index] with { Patch = null, PatchOmitted = true };
            if (Size(detail) <= DetailBytes) return detail;
        }
        throw new CallFailed("This pull request detail is too large. Open it on GitHub.");
    }
    /// <summary>The list row's checks, from the head commit's rollup: GitHub's five states read as four words.</summary>
    private static string RollupWord(JsonElement row)
    {
        var head = row.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Object ? Array(commits, "nodes").LastOrDefault() : default;
        var state = head.ValueKind == JsonValueKind.Object ? Text(head, "commit", "statusCheckRollup", "state") : "";
        return state switch { "SUCCESS" => "passing", "FAILURE" or "ERROR" => "failing", "PENDING" or "EXPECTED" => "pending", _ => "none" };
    }
    /// <summary>An Actions job's details link names its run and job: <c>/{repo}/actions/runs/{run}/job/{job}</c>.</summary>
    private static PullRequestCheck Check(JsonElement row)
    {
        var url = Optional(row, "detailsUrl") ?? Text(row, "targetUrl");
        long? run = null, job = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var link) && link.Host == "github.com")
        {
            var parts = link.AbsolutePath.Trim('/').Split('/');
            if (parts.Length == 7 && parts[2] == "actions" && parts[3] == "runs" && parts[5] == "job" &&
                long.TryParse(parts[4], CultureInfo.InvariantCulture, out var runId) && long.TryParse(parts[6], CultureInfo.InvariantCulture, out var jobId))
                (run, job) = (runId, jobId);
        }
        return new PullRequestCheck(Optional(row, "name") ?? Text(row, "context"), Optional(row, "status") ?? Text(row, "state"), Text(row, "conclusion"), url)
        { Workflow = Text(row, "workflowName"), StartedAt = Text(row, "startedAt"), CompletedAt = Text(row, "completedAt"), RunId = run, JobId = job };
    }
    private async Task<string> Repository(string root, CancellationToken ct)
    {
        var origin = await git.RunAsync(root, ["remote", "get-url", "origin"], null, null, ct);
        if (origin.ExitCode != 0) throw new CallFailed("No origin remote.");
        return GitHubRepository.FromRemoteUrl(origin.Output.Trim()) ?? throw new CallFailed("Origin is not on github.com.");
    }
    private async Task<JsonElement> Json(string root, IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        var run = await gh.RunAsync(root, args, input, Limit, ct);
        if (run.ExitCode != 0) throw new CallFailed("GitHub couldn't complete this request.\n\n" + run.Error.Trim());
        try { using var document = JsonDocument.Parse(run.Output); return document.RootElement.Clone(); }
        catch (JsonException) { throw new CallFailed("GitHub returned invalid JSON."); }
    }
    private static int Size(PullRequestDetail detail) => JsonSerializer.SerializeToUtf8Bytes(detail, PullRequestsJson.Default.PullRequestDetail).Length;
    private static JsonElement Required(JsonElement row, params string[] path)
    {
        foreach (var key in path) if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out row)) throw new CallFailed("GitHub returned incomplete pull request data.");
        return row;
    }
    private static string Text(JsonElement row, params string[] path)
    {
        foreach (var key in path) if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out row)) return "";
        return row.ValueKind == JsonValueKind.String ? row.GetString()! : "";
    }
    private static string? Optional(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Bool(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static int Number(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    private static int? NullableInt(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    private static IEnumerable<JsonElement> Array(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
