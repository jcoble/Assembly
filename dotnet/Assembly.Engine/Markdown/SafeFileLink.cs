namespace Assembly.Engine.Markdown;

/// <summary>A link to a file in the workspace (<c>file:</c> or a bare path like <c>src/app.ts:42</c>).</summary>
public sealed record SafeFileLink(string Path, IReadOnlyList<SafeInline> Parts) : SafeInline;
