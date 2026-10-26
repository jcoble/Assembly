using Markdig.Syntax.Inlines;

using Assembly.Domain.Markdown;
namespace Assembly.Application.Features.Chat.Rows.Markdown;

/// <summary>Markdig's inlines to safe inline parts. Raw HTML stays text; neighbouring text runs are joined.</summary>
internal static class InlineReader
{
    public static IReadOnlyList<SafeInline> Read(ContainerInline? container)
    {
        var parts = new List<SafeInline>();
        for (var inline = container?.FirstChild; inline is not null; inline = inline.NextSibling)
            AddPart(parts, inline);
        return parts.Count == 0 ? [new SafeText("")] : Joined(parts);
    }

    public static string PlainText(IEnumerable<SafeInline> parts) => string.Concat(parts.Select(part => part switch
    {
        SafeText text => text.Value,
        SafeCodeSpan code => code.Value,
        SafeStrong strong => PlainText(strong.Parts),
        SafeEmphasis emphasis => PlainText(emphasis.Parts),
        SafeStrike strike => PlainText(strike.Parts),
        SafeLink link => PlainText(link.Parts),
        SafeFileLink fileLink => PlainText(fileLink.Parts),
        _ => "",
    }));

    private static void AddPart(List<SafeInline> parts, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                parts.Add(new SafeText(literal.Content.ToString()));
                break;
            case CodeInline code:
                parts.Add(new SafeCodeSpan(code.Content));
                break;
            case EmphasisInline emphasis:
                parts.Add(Emphasis(emphasis));
                break;
            case LinkInline { IsImage: true } image:
                var alt = Read(image);
                parts.Add(SafeLinks.Create(image.Url ?? "", alt is [SafeText { Value: "" }] ? [new SafeText(image.Url ?? "")] : alt));
                break;
            case LinkInline link:
                parts.Add(SafeLinks.Create(link.Url ?? "", Read(link)));
                break;
            case AutolinkInline autolink:
                var href = autolink.IsEmail ? $"mailto:{autolink.Url}" : autolink.Url;
                parts.Add(SafeLinks.Create(href, [new SafeText(autolink.Url)]));
                break;
            case LineBreakInline:
                parts.Add(new SafeText("\n"));
                break;
            case HtmlInline html:
                parts.Add(new SafeText(html.Tag));
                break;
            case HtmlEntityInline entity:
                parts.Add(new SafeText(entity.Transcoded.ToString()));
                break;
            case ContainerInline container:
                for (var child = container.FirstChild; child is not null; child = child.NextSibling)
                    AddPart(parts, child);
                break;
        }
    }

    private static SafeInline Emphasis(EmphasisInline emphasis)
    {
        var inner = Read(emphasis);
        return (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
        {
            ('~', _) => new SafeStrike(inner),
            (_, >= 2) => new SafeStrong(inner),
            _ => new SafeEmphasis(inner),
        };
    }

    private static List<SafeInline> Joined(List<SafeInline> parts)
    {
        var joined = new List<SafeInline>(parts.Count);
        foreach (var part in parts)
        {
            if (part is SafeText next && joined.Count > 0 && joined[^1] is SafeText previous)
                joined[^1] = new SafeText(previous.Value + next.Value);
            else
                joined.Add(part);
        }
        return joined;
    }
}
