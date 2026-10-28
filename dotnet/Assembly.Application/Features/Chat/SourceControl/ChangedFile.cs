namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>A changed path with git's index and worktree letters, and its old path when renamed or copied.</summary>
public sealed record ChangedFile(string Path, string? OriginalPath, char Index, char Worktree);
