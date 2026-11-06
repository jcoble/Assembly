namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

public sealed record PullRequestSummary(string Repository, string? LocalRoot, int Number, string Title, string Url, bool IsDraft, string UpdatedAt, string State, string Author, string HeadBranch, string BaseBranch);
public sealed record PullRequestPage(IReadOnlyList<PullRequestSummary> Items, string? Cursor, int Total);
