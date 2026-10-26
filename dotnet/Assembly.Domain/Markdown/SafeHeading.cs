namespace Assembly.Domain.Markdown;

public sealed record SafeHeading(int Level, IReadOnlyList<SafeInline> Parts) : SafeBlock;
