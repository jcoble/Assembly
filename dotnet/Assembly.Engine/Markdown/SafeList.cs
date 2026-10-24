namespace Assembly.Engine.Markdown;

public sealed record SafeList(bool Ordered, IReadOnlyList<SafeListItem> Items) : SafeBlock;
