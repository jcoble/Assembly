using System.Text;
using System.Text.RegularExpressions;
namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

/// <summary>Pull request text as GitHub shows it. Bots write HTML into Markdown (folded details, hidden state comments,
/// relative times, line breaks); the app's Markdown shows HTML as text, so the common tags become their Markdown and the
/// rest are dropped. Code blocks are left as written.</summary>
public static partial class GitHubText
{
    public static string Tidy(string text)
    {
        if (!text.Contains('<', StringComparison.Ordinal)) return text;
        var output = new StringBuilder();
        var prose = new StringBuilder();
        var fenced = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (!fenced) Flush(prose, output);
                fenced = !fenced;
                output.Append(line).Append('\n');
                continue;
            }
            (fenced ? output : prose).Append(line).Append('\n');
        }
        Flush(prose, output);
        return MultipleBlankLines().Replace(output.ToString(), "\n\n").Trim();
    }
    private static void Flush(StringBuilder prose, StringBuilder output)
    {
        if (prose.Length == 0) return;
        var text = HtmlComment().Replace(prose.ToString(), "");
        // The summary is made bold as a whole, so bold inside it is dropped rather than doubled.
        text = Summary().Replace(text, match => "\n**" + Bold().Replace(match.Groups[1].Value, "").Replace("**", "").Trim() + "**\n\n");
        text = Link().Replace(text, match => "[" + match.Groups[2].Value.Trim() + "](" + match.Groups[1].Value + ")");
        text = Image().Replace(text, match => match.Groups[1].Value);
        text = Bold().Replace(text, "**");
        text = Italic().Replace(text, "*");
        text = Code().Replace(text, "`");
        text = Break().Replace(text, "\n");
        text = Item().Replace(text, "\n- ");
        text = Row().Replace(text, "\n");
        text = Cell().Replace(text, " ");
        text = Tag().Replace(text, "");
        output.Append(text);
        prose.Clear();
    }
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();
    [GeneratedRegex("<summary[^>]*>(.*?)</summary>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Summary();
    [GeneratedRegex("<a\\s[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Link();
    [GeneratedRegex("<img\\s[^>]*?(?:alt=\"([^\"]*)\")?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Image();
    [GeneratedRegex("</?(?:b|strong)>", RegexOptions.IgnoreCase)]
    private static partial Regex Bold();
    [GeneratedRegex("</?(?:i|em)>", RegexOptions.IgnoreCase)]
    private static partial Regex Italic();
    [GeneratedRegex("</?code>", RegexOptions.IgnoreCase)]
    private static partial Regex Code();
    [GeneratedRegex("<br\\s*/?>|</?p(?:\\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex Break();
    [GeneratedRegex("<li(?:\\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex Item();
    [GeneratedRegex("</?(?:tr|h[1-6]|ul|ol|hr)(?:\\s[^>]*)?/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Row();
    [GeneratedRegex("</?(?:td|th)(?:\\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex Cell();
    /// <summary>Any other tag GitHub would render rather than show: details, relative times, divs, tables of contents.</summary>
    [GeneratedRegex("</?(?:details|relative-time|div|span|sub|sup|picture|source|kbd|ins|del|table|thead|tbody|li)\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();
    [GeneratedRegex("\n{3,}")]
    private static partial Regex MultipleBlankLines();
}
