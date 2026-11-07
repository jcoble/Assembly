using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows.Markdown;
namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

public sealed partial class PullRequestReader
{
    private const string ThreadsQuery = "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){reviewThreads(first:100){nodes{id isResolved isOutdated path line originalLine diffSide comments(first:100){nodes{databaseId author{login} body createdAt}}}}}}}";
    /// <summary>Line comments come grouped as GitHub's review threads, with whether each is resolved or outdated.
    /// A reply names its thread's first comment, the one GitHub replies to.</summary>
    private async Task<(IReadOnlyList<PullRequestComment> Comments, IReadOnlyList<PullRequestThread> Threads)> Threads(string root, string repository, int number, CancellationToken ct)
    {
        var parts = repository.Split('/');
        var request = new JsonObject { ["query"] = ThreadsQuery, ["variables"] = new JsonObject { ["owner"] = parts[0], ["name"] = parts[1], ["number"] = number } };
        var result = await Json(root, ["api", "graphql", "--input", "-"], request.ToJsonString(), ct);
        if (result.ValueKind != JsonValueKind.Object) throw new CallFailed("GitHub returned invalid review threads.");
        if (result.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw new CallFailed("GitHub couldn't read the review threads: " + Text(errors[0], "message"));
        List<PullRequestComment> comments = [];
        List<PullRequestThread> threads = [];
        foreach (var node in Array(Required(result, "data", "repository", "pullRequest", "reviewThreads"), "nodes"))
        {
            var path = Text(node, "path");
            var line = NullableInt(node, "line") ?? NullableInt(node, "originalLine");
            var side = Text(node, "diffSide");
            long? first = null;
            List<string> ids = [];
            foreach (var row in Array(Required(node, "comments"), "nodes"))
            {
                if (!row.TryGetProperty("databaseId", out var idValue) || idValue.ValueKind != JsonValueKind.Number) continue;
                var id = idValue.GetInt64();
                comments.Add(new PullRequestComment(id.ToString(CultureInfo.InvariantCulture), Text(row, "author", "login"), GitHubText.Tidy(Text(row, "body")), Text(row, "createdAt"), path, line, side, first)
                { BodyBlocks = SafeMarkdown.Parse(GitHubText.Tidy(Text(row, "body"))) });
                first ??= id;
                ids.Add(id.ToString(CultureInfo.InvariantCulture));
            }
            if (ids.Count > 0) threads.Add(new(Text(node, "id"), path, line, side, Bool(node, "isResolved"), Bool(node, "isOutdated"), ids));
        }
        return (comments, threads);
    }
}
