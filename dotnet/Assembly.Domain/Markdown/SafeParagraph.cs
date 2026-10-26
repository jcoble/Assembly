namespace Assembly.Domain.Markdown;

public sealed record SafeParagraph(IReadOnlyList<SafeInline> Parts) : SafeBlock;
