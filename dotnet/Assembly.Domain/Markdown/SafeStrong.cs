namespace Assembly.Domain.Markdown;

public sealed record SafeStrong(IReadOnlyList<SafeInline> Parts) : SafeInline;
