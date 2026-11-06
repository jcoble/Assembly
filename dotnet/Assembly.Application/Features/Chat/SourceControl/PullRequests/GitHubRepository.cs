using System.Text.RegularExpressions;
using Assembly.Application.Common.Calls;
namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

public static class GitHubRepository
{
    public static string Parse(string repository)
    {
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9-]+/[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant) || repository.Split('/')[1] is "." or "..")
            throw new CallFailed("Expected a GitHub repository as owner/name.");
        return repository;
    }
    public static string? FromRemoteUrl(string url)
    {
        string path;
        if (url.StartsWith("git@github.com:", StringComparison.Ordinal)) path = url[15..];
        else
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "github.com" || uri.Scheme is not ("https" or "ssh") || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
            // Uri canonicalizes dot segments; Git remote identity must validate the original path instead.
            var authority = url.IndexOf("://", StringComparison.Ordinal) + 3;
            var slash = url.IndexOf('/', authority);
            if (slash < 0) return null;
            path = url[(slash + 1)..];
        }
        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        try { return Parse(path); } catch (CallFailed) { return null; }
    }
}
