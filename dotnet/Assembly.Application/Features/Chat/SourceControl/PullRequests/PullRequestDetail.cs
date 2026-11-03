using Assembly.Domain.Markdown;

namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

public sealed record PullRequestComment(string Id, string Author, string Body, string CreatedAt, string? Path, int? Line, string? Side, long? ReplyToId) { public IReadOnlyList<SafeBlock> BodyBlocks { get; init; } = []; }
public sealed record PullRequestReview(string Author, string Body, string State, string SubmittedAt) { public IReadOnlyList<SafeBlock> BodyBlocks { get; init; } = []; }
public sealed record PullRequestCheck(string Name, string Status, string Conclusion, string Url);
public sealed record PullRequestCommit(string Oid, string Headline, string Author, string CommittedAt);
public sealed record PullRequestFile(string Path, string? PreviousPath, string Status, int Additions, int Deletions, string? Patch, bool PatchOmitted = false);
public sealed record PullRequestTimeline(string Kind, string Author, string Body, string CreatedAt, string? Sha = null, string? CommentId = null);
public sealed record PullRequestDetail(string Repository, string LocalRoot, int Number, string Title, string Body, string Url, string Author, string HeadBranch, string BaseBranch, string HeadSha, string BaseSha, string State, bool IsDraft, string Mergeable, string ReviewDecision, string CreatedAt, string MergedAt, string MergedBy, string ClosedAt, IReadOnlyList<PullRequestCommit> Commits, IReadOnlyList<PullRequestComment> Comments, IReadOnlyList<PullRequestReview> Reviews, IReadOnlyList<string> Reviewers, IReadOnlyList<PullRequestCheck> Checks, IReadOnlyList<PullRequestFile> Files, bool MoreFiles, IReadOnlyList<PullRequestTimeline> Timeline) { public IReadOnlyList<SafeBlock> BodyBlocks { get; init; } = []; }
public sealed record PrFileContext(string Path, string Patch, bool Binary, bool Truncated);
