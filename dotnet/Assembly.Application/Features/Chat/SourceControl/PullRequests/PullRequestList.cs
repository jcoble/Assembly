namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

public sealed record PullRequestSummary(string Repository, string? LocalRoot, int Number, string Title, string Url, bool IsDraft, string UpdatedAt, string State, string Author, string HeadBranch, string BaseBranch)
{
    /// <summary>The head commit's checks as one word: passing, failing, pending or none.</summary>
    public string? Checks { get; init; }
    /// <summary>GitHub's review decision: APPROVED, CHANGES_REQUESTED, REVIEW_REQUIRED or empty.</summary>
    public string? ReviewDecision { get; init; }
}
public sealed record PullRequestPage(IReadOnlyList<PullRequestSummary> Items, string? Cursor, int Total);
