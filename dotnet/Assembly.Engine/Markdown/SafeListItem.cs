namespace Assembly.Engine.Markdown;

/// <summary>An item's first paragraph is its <c>Parts</c>; anything after it (nested lists, quotes) is its <c>Blocks</c>.</summary>
public sealed record SafeListItem(bool Task, bool Checked, IReadOnlyList<SafeInline> Parts, IReadOnlyList<SafeBlock> Blocks);
