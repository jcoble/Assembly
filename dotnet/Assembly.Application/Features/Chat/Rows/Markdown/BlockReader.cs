using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using Assembly.Domain.Markdown;
namespace Assembly.Application.Features.Chat.Rows.Markdown;

/// <summary>Markdig's block tree to safe blocks. Anything it doesn't know (link definitions, say) is dropped.</summary>
internal sealed class BlockReader(string source)
{
    public IReadOnlyList<SafeBlock> Read(IEnumerable<Block> blocks)
    {
        var safe = new List<SafeBlock>();
        foreach (var block in blocks)
            if (Convert(block) is { } converted)
                safe.Add(converted);
        return safe;
    }

    private SafeBlock? Convert(Block block) => block switch
    {
        ParagraphBlock paragraph => new SafeParagraph(InlineReader.Read(paragraph.Inline)),
        HeadingBlock heading => new SafeHeading(heading.Level, InlineReader.Read(heading.Inline)),
        FencedCodeBlock fenced => new SafeCode(
            FirstWord(fenced.Info), fenced.Lines.ToString(), Complete: fenced.ClosingFencedCharCount > 0),
        CodeBlock code => new SafeCode("", code.Lines.ToString(), Complete: true),
        QuoteBlock quote => new SafeQuote(Read(quote)),
        ListBlock list => new SafeList(list.IsOrdered, [.. list.OfType<ListItemBlock>().Select(ListItem)]),
        Table table => Table(table),
        ThematicBreakBlock => new SafeRule(),
        HtmlBlock html => new SafeParagraph([new SafeText(html.Lines.ToString())]),
        _ => null,
    };

    private SafeListItem ListItem(ListItemBlock item)
    {
        // The first paragraph is the item's own text; everything after it is nested.
        var children = item.ToList();
        var first = children.FirstOrDefault() as ParagraphBlock;
        var task = first?.Inline?.FirstChild as TaskList;
        var parts = first is null ? (IReadOnlyList<SafeInline>)[new SafeText("")] : InlineReader.Read(first.Inline);
        var blocks = Read(first is null ? children : children.Skip(1));

        if (task is null)
            return new SafeListItem(false, false, parts, blocks);

        parts = WithoutTaskBox(parts);
        // A box with nothing after it ("- [x]") isn't a task; it stays literal text.
        if (blocks.Count == 0 && parts is [SafeText { Value: "" }])
            return new SafeListItem(false, false, [new SafeText(source.Substring(task.Span.Start, task.Span.Length))], []);
        return new SafeListItem(true, task.Checked, parts, blocks);
    }

    // Markdig keeps the box as the paragraph's first inline, which reads as no text; drop the space after it.
    private static IReadOnlyList<SafeInline> WithoutTaskBox(IReadOnlyList<SafeInline> parts)
    {
        if (parts.Count == 0 || parts[0] is not SafeText first)
            return parts;
        var trimmed = parts.ToList();
        trimmed[0] = new SafeText(first.Value.TrimStart());
        return trimmed;
    }

    private static SafeTable Table(Table table)
    {
        var headers = new List<IReadOnlyList<SafeInline>>();
        var rows = new List<IReadOnlyList<IReadOnlyList<SafeInline>>>();
        foreach (var row in table.OfType<TableRow>())
        {
            var cells = row.OfType<TableCell>()
                .Select(cell => Trimmed(InlineReader.Read(cell.OfType<ParagraphBlock>().FirstOrDefault()?.Inline)))
                .ToList();
            if (row.IsHeader)
                headers = cells;
            else
                rows.Add(cells);
        }
        return new SafeTable(headers, rows);
    }

    // A cell keeps the spaces before its closing pipe; the table doesn't want them.
    private static IReadOnlyList<SafeInline> Trimmed(IReadOnlyList<SafeInline> parts)
    {
        var trimmed = parts.ToList();
        if (trimmed is [SafeText first, ..])
            trimmed[0] = new SafeText(first.Value.TrimStart());
        if (trimmed is [.., SafeText last])
            trimmed[^1] = new SafeText(last.Value.TrimEnd());
        if (trimmed.Count > 1)
            trimmed.RemoveAll(part => part is SafeText { Value: "" });
        return trimmed;
    }

    private static string FirstWord(string? info) =>
        info?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
}
