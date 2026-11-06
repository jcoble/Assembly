namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>A changed path with git's index and worktree letters, and its old path when renamed or copied. Revision is
/// the working-tree file's size and write time when it was read, so a discard can tell an edit that keeps the letters.</summary>
public sealed record ChangedFile(string Path, string? OriginalPath, char Index, char Worktree, string? Revision = null);
