namespace Assembly.Domain.Markdown;

public sealed record SafeList(bool Ordered, IReadOnlyList<SafeListItem> Items) : SafeBlock;
