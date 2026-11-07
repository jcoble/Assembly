using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Common.GitHub;

namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

public sealed record PullRequestCreated(int Number, string Url, string Head);

/// <summary>Every hosted write is preceded by current GitHub identity checks and is sent exactly once.</summary>
public sealed partial class PullRequestWriter(IGit git, IGh gh, PullRequestReader reader)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);
    public async Task<PullRequestCreated> CreateAsync(string root, string @base, string title, string description, bool draft, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new CallBadArguments("A pull request title is required.");
        var valid = await git.RunAsync(root, ["check-ref-format", "--branch", @base], null, null, ct);
        if (valid.ExitCode != 0) throw new CallBadArguments("Choose a valid base branch.");
        var status = await new GitStatusReader(git).ReadAsync(root, ct);
        if (status.Branch is not { } branch) throw new CallFailed("Create a branch before opening a pull request.");
        var origin = await git.RunAsync(root, ["remote", "get-url", "origin"], null, null, ct);
        var repository = origin.ExitCode == 0 ? GitHubRepository.FromRemoteUrl(origin.Output.Trim()) : null;
        if (repository is null) throw new CallFailed("Origin is not on github.com.");
        var sync = new GitSync(git);
        if (status.HasUpstream) await sync.PushAsync(root, branch, ct);
        else await sync.PublishAsync(root, branch, "origin", ct);
        List<string> args = ["pr", "create", "-R", repository, "--head", branch, "--base", @base, "--title", title.Trim(), "--body-file", "-"];
        if (draft) args.Add("--draft");
        var result = await gh.RunAsync(root, args, description, Limit, ct);
        if (result.ExitCode != 0) throw new CallFailed("Pushed, but the pull request wasn't created: " + result.Error.Trim());
        var url = result.Output.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "https" || parsed.Host != "github.com")
            throw Unconfirmed();
        var parts = parsed.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 4 || !string.Equals(parts[0] + "/" + parts[1], repository, StringComparison.OrdinalIgnoreCase)
            || parts[2] != "pull" || !int.TryParse(parts[3], out var number) || number <= 0) throw Unconfirmed();
        return new(number, url, branch);
    }
    public async Task<string> ReviewAsync(string root, int number, string expectedHead, string body, IReadOnlyList<LineComment> comments, CancellationToken ct, string? expectedRepository = null, Action? ensureCurrent = null, string verdict = "COMMENT")
    {
        if (verdict is not ("COMMENT" or "APPROVE" or "REQUEST_CHANGES")) throw new CallBadArguments("Choose comment, approve or request changes.");
        // An approval may stand alone; a comment or a request for changes says what it's about.
        if (verdict != "APPROVE" || !string.IsNullOrWhiteSpace(body)) Body(body, 20_000, "Write an overall review comment before submitting");
        if (comments.Count > 20) throw new CallBadArguments("Review draft is too large");
        var detail = await Current(root, number, expectedHead, "submitting", ct, expectedRepository);
        foreach (var comment in comments)
        {
            Body(comment.Body, 10_000, "A line comment has an invalid target or body");
            if (!detail.Files.Any(file => file.Path == comment.Path && comment.IsIn(file.Patch)))
                throw new CallFailed("A line comment is outside the current diff. Refresh and inspect it before submitting.");
        }
        return await Write(root, detail, "reviews", "POST", new ReviewPayload(detail.HeadSha, body.Trim(), verdict,
            comments.Select(comment => new ReviewCommentPayload(comment.Path, comment.Line, comment.Side, comment.Body.Trim())).ToArray()),
            PullRequestsJson.Default.ReviewPayload, ct, ensureCurrent: ensureCurrent);
    }
    public async Task<string> ReplyAsync(string root, int number, string expectedHead, long commentId, string body, CancellationToken ct, string? expectedRepository = null, Action? ensureCurrent = null)
    {
        Body(body, 10_000, "Invalid review reply");
        var detail = await Current(root, number, expectedHead, "replying", ct, expectedRepository, closedToo: true);
        if (commentId <= 0 || !detail.Comments.Any(comment => comment.Id == commentId.ToString(CultureInfo.InvariantCulture) && comment.Path is not null && comment.ReplyToId is null))
            throw new CallFailed("That review thread is no longer available. Refresh the PR.");
        return await Write(root, detail, $"comments/{commentId}/replies", "POST", new BodyPayload(body.Trim()), PullRequestsJson.Default.BodyPayload, ct, ensureCurrent: ensureCurrent);
    }
    public async Task<string> CommentAsync(string root, int number, string expectedHead, string body, CancellationToken ct, string? expectedRepository = null, Action? ensureCurrent = null)
    {
        Body(body, 20_000, "Invalid pull request comment");
        var detail = await Current(root, number, expectedHead, "commenting", ct, expectedRepository, closedToo: true);
        return await Write(root, detail, "comments", "POST", new BodyPayload(body.Trim()), PullRequestsJson.Default.BodyPayload, ct, issue: true, ensureCurrent: ensureCurrent);
    }
    public async Task<string> MergeAsync(string root, int number, string method, string expectedHead, string expectedBase, CancellationToken ct, string? expectedRepository = null, Action? ensureCurrent = null)
    {
        if (method is not ("merge" or "squash" or "rebase") || !Sha(expectedBase) || !Sha(expectedHead))
            throw new CallBadArguments("Invalid pull request merge target or method");
        var detail = await Current(root, number, expectedHead, "merging", ct, expectedRepository);
        if (detail.IsDraft || !string.Equals(detail.BaseSha, expectedBase, StringComparison.OrdinalIgnoreCase))
            throw new CallFailed("This pull request changed, closed, or is still a draft. Refresh it before merging.");
        if (detail.Mergeable != "MERGEABLE") throw new CallFailed("GitHub does not report this pull request as mergeable. Refresh it before trying again.");
        var answer = await Send(root, ["api", "--method", "PUT", $"repos/{detail.Repository}/pulls/{number}/merge", "--input", "-"], new MergePayload(expectedHead, method), PullRequestsJson.Default.MergePayload, ct, ensureCurrent);
        if (!answer.TryGetProperty("merged", out var merged)) throw Unconfirmed();
        if (merged.ValueKind == JsonValueKind.False) throw new CallFailed("GitHub refused to merge this pull request. Refresh it before trying again.");
        if (merged.ValueKind != JsonValueKind.True) throw Unconfirmed();
        return detail.Url;
    }
    private async Task<PullRequestDetail> Current(string root, int number, string expectedHead, string action, CancellationToken ct, string? expectedRepository = null, bool closedToo = false)
    {
        if (number <= 0 || !Sha(expectedHead)) throw new CallBadArguments("Invalid pull request target");
        var detail = expectedRepository is null ? await reader.DetailAsync(root, number, ct) :
            await reader.ProjectDetailAsync(root, expectedRepository, number, ct);
        if ((detail.State != "OPEN" && !closedToo) || !string.Equals(detail.HeadSha, expectedHead, StringComparison.OrdinalIgnoreCase))
            throw new CallFailed($"This pull request changed or closed. Refresh it before {action}.");
        return detail;
    }
    private async Task<string> Write<TPayload>(string root, PullRequestDetail detail, string suffix, string method, TPayload payload, JsonTypeInfo<TPayload> metadata, CancellationToken ct, bool issue = false, Action? ensureCurrent = null)
    {
        var answer = await Send(root, ["api", "--method", method, $"repos/{detail.Repository}/{(issue ? "issues" : "pulls")}/{detail.Number}/{suffix}", "--input", "-"], payload, metadata, ct, ensureCurrent);
        if (!answer.TryGetProperty("html_url", out var url) || url.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(url.GetString())) throw Unconfirmed();
        return url.GetString()!;
    }
    private async Task<JsonElement> Send<TPayload>(string root, IReadOnlyList<string> args, TPayload payload, JsonTypeInfo<TPayload> metadata, CancellationToken ct, Action? ensureCurrent = null)
    {
        ct.ThrowIfCancellationRequested();
        ensureCurrent?.Invoke();
        var answer = await gh.RunAsync(root, args, JsonSerializer.Serialize(payload, metadata), Limit, ct);
        if (answer.ExitCode != 0) throw new CallFailed("GitHub refused the write: " + answer.Error.Trim());
        try { using var json = JsonDocument.Parse(answer.Output); if (json.RootElement.ValueKind != JsonValueKind.Object) throw Unconfirmed(); return json.RootElement.Clone(); }
        catch (JsonException) { throw Unconfirmed(); }
    }
    internal sealed record ReviewPayload([property: JsonPropertyName("commit_id")] string CommitId, string Body, string Event,
        IReadOnlyList<ReviewCommentPayload> Comments);
    internal sealed record ReviewCommentPayload(string Path, int Line, string Side, string Body);
    internal sealed record BodyPayload(string Body);
    internal sealed record MergePayload(string Sha, [property: JsonPropertyName("merge_method")] string MergeMethod);

    private static GhUnconfirmed Unconfirmed() => new("GitHub's write was not confirmed. Refresh before trying again.");
    private static bool Sha(string text) => text.Length == 40 && text.All(char.IsAsciiHexDigit);
    private static void Body(string text, int maximum, string empty)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new CallBadArguments(empty);
        if (Encoding.UTF8.GetByteCount(text) > maximum) throw new CallBadArguments("Comment or review draft is too large");
    }
}
