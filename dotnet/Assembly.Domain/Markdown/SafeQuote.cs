namespace Assembly.Domain.Markdown;

public sealed record SafeQuote(IReadOnlyList<SafeBlock> Blocks) : SafeBlock;
