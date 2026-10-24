namespace Assembly.Engine.Markdown;

public sealed record SafeQuote(IReadOnlyList<SafeBlock> Blocks) : SafeBlock;
