namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>The default branch's name and the branch's diff from their merge base, with a size notice.</summary>
public sealed record GitBranchDiff(string Base, string Diff, bool Truncated);
