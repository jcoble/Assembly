namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>One file's staged and working tree diffs, with binary and size notices.</summary>
public sealed record GitFileDiff(string Path, string Staged, string Unstaged, bool Binary, bool Truncated);
