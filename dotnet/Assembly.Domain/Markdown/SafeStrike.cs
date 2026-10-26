namespace Assembly.Domain.Markdown;

public sealed record SafeStrike(IReadOnlyList<SafeInline> Parts) : SafeInline;
