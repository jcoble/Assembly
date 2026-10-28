namespace Assembly.Application.Features.Worktrees;

/// <summary>
/// One worktree as the Worktrees pane shows it (docs/behavior/scm.md). Branch is null when detached, Head when bare or
/// before the first commit; Unpushed counts HEAD's commits that no remote has; HeadCommittedAt is unix milliseconds.
/// </summary>
public sealed record Worktree(string Path, string? Branch, string? Head, bool IsMain, bool IsBare, bool IsDirty,
    int Unpushed, bool Locked, bool Prunable, string? HeadSubject, long? HeadCommittedAt);
