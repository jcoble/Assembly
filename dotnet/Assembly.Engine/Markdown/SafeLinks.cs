namespace Assembly.Engine.Markdown;

/// <summary>
/// Decides what a link becomes. Web links only for http, https, mailto and in-page anchors; workspace paths
/// and <c>file:</c> links become file links; anything else (javascript:, data:, …) becomes plain text.
/// The same rules as Assembly's safe_markdown.rs.
/// </summary>
internal static class SafeLinks
{
    public static SafeInline Create(string href, IReadOnlyList<SafeInline> parts)
    {
        var trimmed = href.Trim();
        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return FilePath(StripFileScheme(trimmed)) is { } filePath ? new SafeFileLink(filePath, parts) : AsText(parts);

        if (IsWebLink(trimmed) && !HasControlCharacters(trimmed))
            return new SafeLink(trimmed, parts);

        return FilePath(trimmed) is { } path ? new SafeFileLink(path, parts) : AsText(parts);
    }

    private static bool IsWebLink(string href) =>
        href.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith('#');

    private static string StripFileScheme(string href) =>
        href.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? href[7..] : href[5..];

    private static string? FilePath(string href)
    {
        var path = href.Trim();
        return path.Length == 0 || HasSchemePrefix(path) || HasControlCharacters(path) ? null : path;
    }

    /// <summary><c>scheme:</c> before any other character, as in <c>javascript:</c>. <c>src/app.ts:42</c> has a slash first, so it isn't one.</summary>
    private static bool HasSchemePrefix(string href)
    {
        var colon = href.IndexOf(':');
        return colon > 0 && href[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '.' or '-');
    }

    private static bool HasControlCharacters(string text) => text.Any(c => char.IsControl(c) && c is not '\t' and not '\n');

    private static SafeText AsText(IReadOnlyList<SafeInline> parts) => new(InlineReader.PlainText(parts));
}
