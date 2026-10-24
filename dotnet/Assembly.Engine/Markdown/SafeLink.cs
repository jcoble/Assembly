namespace Assembly.Engine.Markdown;

/// <summary>A web link. Only http, https, mailto and in-page (#) links get here; anything else becomes text.</summary>
public sealed record SafeLink(string Href, IReadOnlyList<SafeInline> Parts) : SafeInline;
