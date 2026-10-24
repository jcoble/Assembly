namespace Assembly.Engine.Markdown;

public sealed record SafeStrong(IReadOnlyList<SafeInline> Parts) : SafeInline;
