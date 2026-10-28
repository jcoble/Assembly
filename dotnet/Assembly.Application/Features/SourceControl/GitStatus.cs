namespace Assembly.Application.Features.SourceControl;

/// <summary>The branch, upstream counts and at most 1000 changed files, with the total before that limit.</summary>
public sealed record GitStatus(string? Branch, string? Head, int Ahead, int Behind,
    bool HasUpstream, IReadOnlyList<ChangedFile> Files, int TotalFiles);
