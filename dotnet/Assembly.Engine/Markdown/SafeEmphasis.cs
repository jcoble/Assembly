namespace Assembly.Engine.Markdown;

public sealed record SafeEmphasis(IReadOnlyList<SafeInline> Parts) : SafeInline;
