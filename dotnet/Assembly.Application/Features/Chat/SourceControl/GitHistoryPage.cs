namespace Assembly.Application.Features.Chat.SourceControl;

public sealed record GitCommit(string ShortSha, string Sha, string Subject, string Body, string Author,
    string CommittedAt, IReadOnlyList<string> Refs, IReadOnlyList<string> Parents);
public sealed record GitHistoryPage(IReadOnlyList<GitCommit> Commits, string? NextCursor);
public sealed record GitCommitFile(string Path, string? OriginalPath, char Letter);
