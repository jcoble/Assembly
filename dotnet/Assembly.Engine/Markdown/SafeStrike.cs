namespace Assembly.Engine.Markdown;

public sealed record SafeStrike(IReadOnlyList<SafeInline> Parts) : SafeInline;
