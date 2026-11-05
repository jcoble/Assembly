using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows.Context;

/// <summary>
/// The paths a prompt's text names and the images its trailing "Attached image:" lines attach. The line rule matches
/// the desktop's <c>attachment.rs</c> (<c>split_lines</c>, <c>reference</c>); both run the same test lines.
/// </summary>
public static class PromptPaths
{
    private const string Prefix = "Attached image: ";
    private const string Thumbnail = ") thumbnail: ";
    private const int MostPaths = 20;

    public static (IReadOnlyList<string> Named, IReadOnlyList<AttachedImage> Attached) Of(string text)
    {
        var lines = text.Split('\n');
        var attached = new List<AttachedImage>();
        var end = lines.Length;
        while (end > 0 && Reference(lines[end - 1]) is { } image)
        {
            attached.Insert(0, image);
            end--;
        }
        return (Named(string.Join('\n', lines[..end])), attached);
    }

    /// <summary>One "Attached image: /path (name)" line, with " thumbnail: /path" after it when the old app wrote one.</summary>
    private static AttachedImage? Reference(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var body = line[Prefix.Length..];
        var space = body.IndexOf(' ');
        if (space < 0 || !Absolute(body[..space]) || space + 1 >= body.Length || body[space + 1] != '(')
            return null;
        var path = body[..space];
        var rest = body[(space + 2)..];
        if (rest.EndsWith(')'))
            return new AttachedImage(path, rest[..^1]);
        var thumbnail = rest.LastIndexOf(Thumbnail, StringComparison.Ordinal);
        return thumbnail >= 0 && Absolute(rest[(thumbnail + Thumbnail.Length)..]) ? new AttachedImage(path, rest[..thumbnail]) : null;
    }

    private static bool Absolute(string path) => path.StartsWith('/') && !path.Any(char.IsWhiteSpace);

    private static List<string> Named(string text)
    {
        var named = new List<string>();
        foreach (var word in Words(text))
        {
            // A leading dot stays, so "./a" and "../a" aren't read as the absolute "/a".
            var path = word.TrimStart('(', ')', ',', ';', ':', '\'', '"', '`').TrimEnd('(', ')', ',', '.', ';', ':', '\'', '"', '`');
            if ((path.StartsWith('/') || path.StartsWith("~/", StringComparison.Ordinal)) && !path.Contains("://") && !named.Contains(path))
                named.Add(path);
            if (named.Count == MostPaths)
                break;
        }
        return named;
    }

    /// <summary>Splits on whitespace outside double quotes, keeping the quotes on the word.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var start = -1;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            if (char.IsWhiteSpace(text[i]) && !quoted)
            {
                if (start >= 0) yield return text[start..i];
                start = -1;
            }
            else if (start < 0)
                start = i;
        }
        if (start >= 0) yield return text[start..];
    }
}
