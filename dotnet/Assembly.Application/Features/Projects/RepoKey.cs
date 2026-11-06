namespace Assembly.Application.Features.Projects;

/// <summary>The old inspector's grouping rules, independent of project identity.</summary>
public static class RepoKey
{
    public static string? Normalize(string? remoteUrl)
    {
        var url = remoteUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return null;
        string authority, path;
        if (url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("ssh://", StringComparison.Ordinal))
        {
            var rest = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var slash = rest.IndexOf('/');
            if (slash < 0) return null;
            authority = rest[..slash]; path = rest[(slash + 1)..];
        }
        else
        {
            var colon = url.IndexOf(':');
            if (colon < 0 || !url[..colon].Contains('@') || url[..colon].Contains('/')) return null;
            authority = url[..colon]; path = url[(colon + 1)..];
        }
        var host = authority[(authority.LastIndexOf('@') + 1)..].Split(':')[0];
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4].TrimEnd('/');
        return host.Length == 0 || path.Length == 0 ? null : $"{host.ToLowerInvariant()}/{path}";
    }
}
