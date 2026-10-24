namespace Assembly.Engine.Markdown;

public sealed record SafeHeading(int Level, IReadOnlyList<SafeInline> Parts) : SafeBlock;
