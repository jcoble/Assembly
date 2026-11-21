using System.Text.RegularExpressions;

namespace Assembly.Application.Common.Agents;

/// <summary>
/// Turns a check's raw error output into the scrubbed detail a person may see. Anything that could be a
/// credential is removed: values from the captured environment, values named as tokens, secrets or keys,
/// long token-shaped runs, and URL query strings. Unrecognised shapes fail closed (redacted), never through.
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
}
