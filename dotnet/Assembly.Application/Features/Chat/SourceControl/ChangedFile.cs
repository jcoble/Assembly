using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>A changed path with git's index and worktree letters, its old path when renamed or copied, its working-tree
/// revision when read, and per-side text line counts (omitted for untracked, binary or capped reads).</summary>
public sealed record ChangedFile(string Path, string? OriginalPath, char Index, char Worktree, string? Revision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StagedAdded = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StagedRemoved = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UnstagedAdded = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UnstagedRemoved = null);
