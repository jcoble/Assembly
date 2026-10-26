namespace Assembly.Domain.Markdown;

public sealed record SafeTable(
    IReadOnlyList<IReadOnlyList<SafeInline>> Headers,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<SafeInline>>> Rows) : SafeBlock;
