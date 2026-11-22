using System.Text.RegularExpressions;

namespace Assembly.Application.Common.Agents;

/// <summary>
/// Turns a check's raw error output into the scrubbed detail a person may see (the Details view only;
/// pushed failure text is a fixed sentence, never this). Anything that could be a credential is removed:
/// values from the captured environment, values named as tokens, secrets or keys, long token-shaped runs,
/// and URL query strings. Home paths become <c>~</c>. Unrecognised token shapes fail closed (redacted).
/// </summary>
public static partial class ProbeDetail
{
    private const int Limit = 1000;
    private const string Redacted = "[redacted]";

    public static string? Scrub(string? raw, IReadOnlyDictionary<string, string>? environment)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw;
        if (environment is not null)
        {
            foreach (var value in environment.Values)
            {
                if (value.Length >= 4)
                    text = text.Replace(value, Redacted, StringComparison.Ordinal);
            }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length >= 4)
            text = text.Replace(home, "~", StringComparison.Ordinal);
        text = HomePath().Replace(text, "~");
        text = UrlQuery().Replace(text, "$1?" + Redacted);
        text = NamedValue().Replace(text, match => match.Groups[1].Value + match.Groups[2].Value + Redacted);
        text = BearerValue().Replace(text, "$1" + Redacted);
        text = LongRun().Replace(text, Redacted);
        var kept = new string(text.Where(character => character is '\n' or '\t' || !char.IsControl(character)).ToArray()).Trim();
        if (kept.Length == 0) return null;
        return kept.Length <= Limit ? kept : kept[..Limit] + "…";
    }

    [GeneratedRegex(@"(https?://[^\s?]+)\?[^\s]*")]
    private static partial Regex UrlQuery();

    [GeneratedRegex("""(?i)(token|secret|password|passwd|api[-_ ]?key|access[-_ ]?key|session[-_ ]?key|auth)(["']?\s*[:=]\s*["']?)[^\s"',}\]]+""")]
    private static partial Regex NamedValue();

    [GeneratedRegex(@"(?i)(bearer\s+)[^\s]+")]
    private static partial Regex BearerValue();

    [GeneratedRegex(@"[A-Za-z0-9_\-+/=]{24,}")]
    private static partial Regex LongRun();

    [GeneratedRegex(@"(?:/Users/|/home/|[A-Za-z]:\\Users\\)[^/\\\s""':]+")]
    private static partial Regex HomePath();
}
