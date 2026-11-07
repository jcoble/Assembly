using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.GitHub;

namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

/// <summary>The writes that keep a pull request moving without the website: threads, draft state, the branch and its checks.</summary>
public sealed partial class PullRequestWriter
{
    private const string ResolveMutation = "mutation($id:ID!){resolveReviewThread(input:{threadId:$id}){thread{isResolved}}}";
    private const string ReopenMutation = "mutation($id:ID!){unresolveReviewThread(input:{threadId:$id}){thread{isResolved}}}";
    /// <summary>Resolves or reopens one of the pull request's review threads. Closed pull requests keep their threads.</summary>
    public async Task<string> ResolveThreadAsync(string root, int number, string expectedHead, string threadId, bool resolved, CancellationToken ct, string? expectedRepository = null, Action? ensureCurrent = null)
    {
        var detail = await Current(root, number, expectedHead, resolved ? "resolving" : "reopening", ct, expectedRepository, closedToo: true);
        var thread = detail.Threads.FirstOrDefault(thread => thread.Id == threadId) ?? throw new CallFailed("That thread isn't on this pull request now. Refresh it.");
        if (thread.Resolved == resolved) return detail.Url;
        var name = resolved ? "resolveReviewThread" : "unresolveReviewThread";
        var request = new JsonObject { ["query"] = resolved ? ResolveMutation : ReopenMutation, ["variables"] = new JsonObject { ["id"] = threadId } };
        var answer = await Raw(root, ["api", "graphql", "--input", "-"], request.ToJsonString(), ct, ensureCurrent);
        try
        {
            using var json = JsonDocument.Parse(answer);
            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                throw new CallFailed("GitHub refused: " + (errors[0].TryGetProperty("message", out var message) ? message.GetString() : "unknown error"));
            if (json.RootElement.GetProperty("data").GetProperty(name).GetProperty("thread").GetProperty("isResolved").GetBoolean() != resolved) throw Unconfirmed();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { throw Unconfirmed(); }
        return detail.Url;
    }
    /// <summary>Marks a draft ready for review, or turns it back into a draft.</summary>
    public async Task<string> ReadyAsync(string root, int number, string expectedHead, bool ready, CancellationToken ct, string? expectedRepository = null, Action? ensureCurrent = null)
    {
        var detail = await Current(root, number, expectedHead, ready ? "marking it ready" : "turning it into a draft", ct, expectedRepository);
        if (detail.IsDraft != ready) return detail.Url;
        List<string> args = ["pr", "ready", number.ToString(CultureInfo.InvariantCulture), "-R", detail.Repository];
        if (!ready) args.Add("--undo");
        await Raw(root, args, null, ct, ensureCurrent);
        return detail.Url;
    }
    /// <summary>Merges the base into the head on GitHub, only while the head is still the commit the person saw.</summary>
    public async Task<string> UpdateBranchAsync(string root, int number, string expectedHead, CancellationToken ct, string? expectedRepository = null, Action? ensureCurrent = null)
    {
        var detail = await Current(root, number, expectedHead, "updating its branch", ct, expectedRepository);
        var answer = await Send(root, ["api", "--method", "PUT", $"repos/{detail.Repository}/pulls/{number}/update-branch", "--input", "-"],
            new UpdateBranchPayload(detail.HeadSha), PullRequestsJson.Default.UpdateBranchPayload, ct, ensureCurrent);
        if (!answer.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) throw Unconfirmed();
        return message.GetString()!;
    }
    /// <summary>Runs a workflow run's failed jobs again. The run must be one of this pull request's current checks.</summary>
    public async Task<string> RerunFailedAsync(string root, int number, string expectedHead, long runId, CancellationToken ct, string? expectedRepository = null, Action? ensureCurrent = null)
    {
        var detail = await Current(root, number, expectedHead, "re-running its checks", ct, expectedRepository);
        if (runId <= 0 || !detail.Checks.Any(check => check.RunId == runId)) throw new CallFailed("That run isn't one of this pull request's checks now. Refresh it.");
        // GitHub answers this with an empty body; a clean exit is its confirmation.
        await Raw(root, ["api", "--method", "POST", $"repos/{detail.Repository}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}/rerun-failed-jobs"], null, ct, ensureCurrent);
        return detail.Url;
    }
    private async Task<string> Raw(string root, IReadOnlyList<string> args, string? input, CancellationToken ct, Action? ensureCurrent)
    {
        ct.ThrowIfCancellationRequested();
        ensureCurrent?.Invoke();
        var answer = await gh.RunAsync(root, args, input, Limit, ct);
        if (answer.ExitCode != 0) throw new CallFailed("GitHub refused: " + answer.Error.Trim());
        return answer.Output;
    }
    internal sealed record UpdateBranchPayload([property: System.Text.Json.Serialization.JsonPropertyName("expected_head_sha")] string ExpectedHeadSha);
}
