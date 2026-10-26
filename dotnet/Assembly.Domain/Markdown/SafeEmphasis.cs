namespace Assembly.Domain.Markdown;

public sealed record SafeEmphasis(IReadOnlyList<SafeInline> Parts) : SafeInline;
