using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Engine.Handoff;

/// <summary>Exact output validation around the existing Git process and cancellation owner.</summary>
internal static class HandoffGit
{
    internal static readonly GitCli Cli = new();

    internal static async Task<List<string>> Records(string folder, string[] args, CancellationToken ct)
    {
        var result = new List<string>();
        await foreach (var value in Cli.ReadAsync(folder, args, null, ct)) result.Add(value);
        return result;
    }

    internal static async Task<string> Scalar(string folder, string[] args, CancellationToken ct)
    {
        var values = await Records(folder, args, ct);
        return values.Count == 1 ? values[0].TrimEnd('\r', '\n') : "";
    }

    internal static bool IsOid(string? value) => value is { Length: 40 or 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsRelativeFileUrl(string url)
    {
        var colon = url.IndexOf(':');
        var slash = url.IndexOf('/');
        return !Path.IsPathRooted(url) && (colon < 0 || slash >= 0 && slash < colon);
    }

    internal static bool IsUrl(string value) => value.Length > 0 && value[0] != '-' && !value.Any(char.IsControl);

    internal static bool IsBranch(string? value) => !string.IsNullOrEmpty(value) && value != "@" &&
        !value.StartsWith('-') && !value.Contains("..", StringComparison.Ordinal) && !value.Contains("@{", StringComparison.Ordinal) &&
        !value.EndsWith('.') && !value.Any(c => c <= ' ' || c == '\u007f' || "~^:?*[\\".Contains(c)) &&
        value.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));

    internal static void NoLinks(string path)
    {
        for (string? at = Path.GetFullPath(path); at is not null; at = Path.GetDirectoryName(at))
            if (new FileInfo(at).LinkTarget is not null) throw new HandoffRefused("bad_args");
    }

    internal static string RepoName(string url)
    {
        // Only the display/destination basename is sanitized. The fetch URL itself is never rewritten.
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) && url.StartsWith(uri.Scheme + ":", StringComparison.OrdinalIgnoreCase)
            ? uri.GetComponents(UriComponents.Path, UriFormat.Unescaped) : url;
        var name = path.TrimEnd('/').Split('/')[^1];
        if (name.Contains(':')) name = name[(name.LastIndexOf(':') + 1)..];
        if (name.EndsWith(".git", StringComparison.Ordinal)) name = name[..^4];
        name = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        if (name.Length == 0 || name is "." or "..") throw new HandoffRefused("bad_args");
        return name;
    }
}
