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
    private const string ViewFields = "number,title,body,url,author,headRefName,baseRefName,headRefOid,baseRefOid,state,isDraft,mergeable,reviewDecision,createdAt,mergedAt,mergedBy,closedAt,commits,comments,reviews,reviewRequests,statusCheckRollup";
    private const string SearchQuery = "query($query:String!,$first:Int!,$after:String){search(query:$query,type:ISSUE_ADVANCED,first:$first,after:$after){issueCount pageInfo{hasNextPage endCursor} nodes{... on PullRequest{number title url isDraft updatedAt state headRefName baseRefName repository{nameWithOwner} author{login}}}}}";
    public async Task<PullRequestPage> ListForRootAsync(string root, string state, string? search, string? cursor, CancellationToken ct) =>
        await ListAsync([await Repository(root, ct)], state, search, cursor, ct);
    public async Task<PullRequestPage> ListAsync(IReadOnlyList<string> repositories, string state, string? search, string? cursor, CancellationToken ct)
    {
        if (repositories.Count > 32) throw new CallBadArguments("Choose at most 32 repositories.");
        var scopes = repositories.Select(GitHubRepository.Parse).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (scopes.Length == 0) return new([], null, 0);
        var terms = state switch { "open" => "is:open", "mine" => "is:open author:@me", "review" => "is:open review-requested:@me", _ => throw new CallBadArguments("Choose Open, Mine or Needs review.") };
        var query = "is:pr " + (scopes.Length > 1 ? "(" + string.Join(" OR ", scopes.Select(repo => "repo:" + repo)) + ")" : "repo:" + scopes[0]) + " " + terms;
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
            items.Add(new(repository, null, Number(row, "number"), Text(row, "title"), Text(row, "url"), Bool(row, "isDraft"), Text(row, "updatedAt"), Text(row, "state"), Text(row, "author", "login"), Text(row, "headRefName"), Text(row, "baseRefName")));
        }
        var info = Required(found, "pageInfo");
        return new(items, Bool(info, "hasNextPage") ? Text(info, "endCursor") : null, Number(found, "issueCount"));
    }
    public async Task<PullRequestDetail> DetailAsync(string root, int number, CancellationToken ct)
    {
        if (number <= 0) throw new CallBadArguments("Pull request number must be positive.");
        var repository = await Repository(root, ct);
        var view = await Json(root, ["pr", "view", number.ToString(CultureInfo.InvariantCulture), "-R", repository, "--json", ViewFields], null, ct);
        if (Number(view, "number") != number) throw new CallFailed("GitHub returned the wrong pull request.");
        var changed = await Json(root, ["api", $"repos/{repository}/pulls/{number}/files?per_page=100&page=1"], null, ct);
        var inline = await Json(root, ["api", $"repos/{repository}/pulls/{number}/comments?per_page=100&page=1"], null, ct);
        if (changed.ValueKind != JsonValueKind.Array || inline.ValueKind != JsonValueKind.Array) throw new CallFailed("GitHub returned invalid pull request files or comments.");
        var files = changed.EnumerateArray().Select(row => new PullRequestFile(Text(row, "filename"), Optional(row, "previous_filename"), Text(row, "status"), Number(row, "additions"), Number(row, "deletions"), Optional(row, "patch"))).ToArray();
        var comments = Array(view, "comments").Select(row => new PullRequestComment(Text(row, "id"), Text(row, "author", "login"), Text(row, "body"), Text(row, "createdAt"), null, null, null, null) { BodyBlocks = SafeMarkdown.Parse(Text(row, "body")) }).Concat(inline.EnumerateArray().Select(row =>
            new PullRequestComment(Required(row, "id").ToString(), Text(row, "user", "login"), Text(row, "body"), Text(row, "created_at"), Optional(row, "path"), NullableInt(row, "line"), Optional(row, "side"), NullableLong(row, "in_reply_to_id")) { BodyBlocks = SafeMarkdown.Parse(Text(row, "body")) })).ToArray();
        var reviews = Array(view, "reviews").Select(row => new PullRequestReview(Text(row, "author", "login"), Text(row, "body"), Text(row, "state"), Text(row, "submittedAt")) { BodyBlocks = SafeMarkdown.Parse(Text(row, "body")) }).ToArray();
        var commits = Array(view, "commits").Select(row => new PullRequestCommit(Text(row, "oid"), Text(row, "messageHeadline"), Array(row, "authors").Select(author => Text(author, "login")).FirstOrDefault() ?? "", Text(row, "committedDate"))).ToArray();
        var reviewers = Array(view, "reviewRequests").Select(row => Optional(row, "login") ?? Text(row, "name")).ToArray();
        var checks = Array(view, "statusCheckRollup").Select(row => new PullRequestCheck(Optional(row, "name") ?? Text(row, "context"), Optional(row, "status") ?? Text(row, "state"), Text(row, "conclusion"), Optional(row, "detailsUrl") ?? Text(row, "targetUrl"))).ToArray();
        var timeline = comments.Select(comment => new PullRequestTimeline("comment", comment.Author, comment.Body, comment.CreatedAt, CommentId: comment.Id))
            .Concat(reviews.Select(review => new PullRequestTimeline("review", review.Author, review.Body, review.SubmittedAt)))
            .Concat(commits.Select(commit => new PullRequestTimeline("commit", commit.Author, commit.Headline, commit.CommittedAt, commit.Oid)))
            .OrderBy(item => DateTimeOffset.TryParse(item.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTimeOffset.MinValue).ToArray();
        var detail = new PullRequestDetail(repository, root, number, Text(view, "title"), Text(view, "body"), Text(view, "url"), Text(view, "author", "login"), Text(view, "headRefName"), Text(view, "baseRefName"), Text(view, "headRefOid"), Text(view, "baseRefOid"), Text(view, "state"), Bool(view, "isDraft"), Text(view, "mergeable"), Text(view, "reviewDecision"), Text(view, "createdAt"), Text(view, "mergedAt"), Text(view, "mergedBy", "login"), Text(view, "closedAt"), commits, comments, reviews, reviewers, checks, files, files.Length == 100, timeline) { BodyBlocks = SafeMarkdown.Parse(Text(view, "body")) };
        if (Size(detail) <= DetailBytes) return detail;
        foreach (var index in Enumerable.Range(0, files.Length).OrderByDescending(index => files[index].Patch?.Length ?? 0))
        {
            if (files[index].Patch is null) continue;
            files[index] = files[index] with { Patch = null, PatchOmitted = true };
            if (Size(detail) <= DetailBytes) return detail;
        }
        throw new CallFailed("This pull request detail is too large. Open it on GitHub.");
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
    private static long? NullableLong(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
    private static IEnumerable<JsonElement> Array(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
