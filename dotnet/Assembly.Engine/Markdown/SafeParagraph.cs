namespace Assembly.Engine.Markdown;

public sealed record SafeParagraph(IReadOnlyList<SafeInline> Parts) : SafeBlock;
