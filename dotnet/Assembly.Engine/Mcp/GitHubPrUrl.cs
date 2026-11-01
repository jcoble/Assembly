using System.Globalization;

namespace Assembly.Engine.Mcp;

public static class GitHubPrUrl
{
    public static (string Repo, int Number)? Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            url.Contains('%') || url.Contains('\\') || url.Any(char.IsWhiteSpace)) return null;
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 5 || parts[0] != "" || parts[3] != "pull" ||
            !Segment(parts[1]) || !Segment(parts[2]) ||
            !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            return null;
        return ($"{parts[1]}/{parts[2]}", number);
    }

    private static bool Segment(string text) => text.Length > 0 && text is not ("." or "..") &&
        text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
