namespace Assembly.Engine.Markdown;

public sealed record SafeTable(
    IReadOnlyList<IReadOnlyList<SafeInline>> Headers,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<SafeInline>>> Rows) : SafeBlock;
