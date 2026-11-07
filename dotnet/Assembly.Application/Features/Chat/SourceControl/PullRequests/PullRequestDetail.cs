using Assembly.Domain.Markdown;

namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

public sealed record PullRequestComment(string Id, string Author, string Body, string CreatedAt, string? Path, int? Line, string? Side, long? ReplyToId) { public IReadOnlyList<SafeBlock> BodyBlocks { get; init; } = []; }
public sealed record PullRequestReview(string Author, string Body, string State, string SubmittedAt) { public IReadOnlyList<SafeBlock> BodyBlocks { get; init; } = []; }
/// <summary>A check as GitHub rolls it up. An Actions job also names its workflow, run and job, so its log and re-run stay in the app.</summary>
public sealed record PullRequestCheck(string Name, string Status, string Conclusion, string Url)
{
    public string Workflow { get; init; } = "";
    public string StartedAt { get; init; } = "";
    public string CompletedAt { get; init; } = "";
    public long? RunId { get; init; }
    public long? JobId { get; init; }
}
/// <summary>A review conversation on a line. Its comments are in the detail's comments, the first one opening it.</summary>
public sealed record PullRequestThread(string Id, string Path, int? Line, string Side, bool Resolved, bool Outdated, IReadOnlyList<string> CommentIds);
/// <summary>A failed job's log, cut to its failing step with timestamps and colour codes taken out.</summary>
public sealed record PrCheckLog(long JobId, string Step, IReadOnlyList<string> Steps, IReadOnlyList<string> Lines, bool Cut);
/// <summary>One file that conflicts when the head merges into the base, with the conflicted regions as Git marks them.</summary>
public sealed record PrConflictFile(string Path, IReadOnlyList<string> Regions, bool Cut);
public sealed record PrConflicts(string BaseSha, string HeadSha, IReadOnlyList<PrConflictFile> Files);
public sealed record PullRequestCommit(string Oid, string Headline, string Author, string CommittedAt);
public sealed record PullRequestFile(string Path, string? PreviousPath, string Status, int Additions, int Deletions, string? Patch, bool PatchOmitted = false);
public sealed record PullRequestTimeline(string Kind, string Author, string Body, string CreatedAt, string? Sha = null, string? CommentId = null);
public sealed record PullRequestDetail(string Repository, string LocalRoot, int Number, string Title, string Body, string Url, string Author, string HeadBranch, string BaseBranch, string HeadSha, string BaseSha, string State, bool IsDraft, string Mergeable, string ReviewDecision, string CreatedAt, string MergedAt, string MergedBy, string ClosedAt, IReadOnlyList<PullRequestCommit> Commits, IReadOnlyList<PullRequestComment> Comments, IReadOnlyList<PullRequestReview> Reviews, IReadOnlyList<string> Reviewers, IReadOnlyList<PullRequestCheck> Checks, IReadOnlyList<PullRequestFile> Files, bool MoreFiles, IReadOnlyList<PullRequestTimeline> Timeline)
{
    public IReadOnlyList<SafeBlock> BodyBlocks { get; init; } = [];
    /// <summary>GitHub's merge state: CLEAN, BEHIND, BLOCKED, DIRTY, DRAFT, HAS_HOOKS, UNSTABLE or UNKNOWN.</summary>
    public string MergeState { get; init; } = "";
    public IReadOnlyList<PullRequestThread> Threads { get; init; } = [];
}
public sealed record PrFileContext(string Path, string Patch, bool Binary, bool Truncated);
