using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assembly.Application.Features.Notion;

public static partial class NotionBody
{
    public static string Plain(JsonElement rich) => string.Concat(rich.EnumerateArray().Select(PlainItem));
    private static string PlainItem(JsonElement item) => item.TryGetProperty("plain_text", out var plain) ? plain.GetString() ?? "" :
        item.TryGetProperty("text", out var text) && text.TryGetProperty("content", out var content) ? content.GetString() ?? "" :
        item.TryGetProperty("mention", out var mention) ? Mention(mention) : "";
    private static string Mention(JsonElement value)
    {
        if (value.TryGetProperty("page", out var page)) return "https://www.notion.so/" + NotionIds.Canonical(page.GetProperty("id").GetString()!).Replace("-", "", StringComparison.Ordinal);
        if (value.TryGetProperty("date", out var date)) return date.GetProperty("start").GetString() ?? "";
        throw new NotionException("notion_unsupported_body");
    }
    public static string Escape(string text)
    {
        var result = new StringBuilder();
        foreach (var c in text) { if ("\\`*_{}[]<>|#!".Contains(c)) result.Append('\\'); result.Append(c); }
        return result.ToString();
    }
    private static string Rich(JsonElement data)
    {
        if (!data.TryGetProperty("rich_text", out var rich)) return "";
        return string.Concat(rich.EnumerateArray().Select(item =>
        {
            var text = Escape(PlainItem(item));
            if (item.TryGetProperty("annotations", out var a))
            {
                if (Flag(a, "code")) text = Fence(PlainItem(item), '`', 1) + PlainItem(item) + Fence(PlainItem(item), '`', 1);
                if (Flag(a, "bold")) text = "**" + text + "**";
                if (Flag(a, "italic")) text = "*" + text + "*";
                if (Flag(a, "strikethrough")) text = "~~" + text + "~~";
            }
            if (item.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String)
            {
                var link = href.GetString()!;
                if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") text = "[" + text + "](<" + link.Replace(">", "%3E", StringComparison.Ordinal) + ">)";
            }
            return text;
        }));
    }
    private static bool Flag(JsonElement value, string key) => value.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True;
    [GeneratedRegex("`+")] private static partial Regex Backticks();
    private static string Fence(string text, char character, int minimum) => new(character, Math.Max(minimum, Backticks().Matches(text).Select(x => x.Length + 1).DefaultIfEmpty(1).Max()));
    public static string Markdown(IReadOnlyList<NotionBlock> blocks, string pageUrl)
    {
        var body = new StringBuilder();
        Append(blocks, pageUrl, body, 0);
        var result = body.ToString().TrimEnd('\n');
        if (Encoding.UTF8.GetByteCount(result) > 256 * 1024) throw new NotionException("notion_too_large");
        return result;
    }
    private static void Append(IReadOnlyList<NotionBlock> blocks, string url, StringBuilder body, int depth)
    {
        if (depth > 32) throw new NotionException("notion_too_large");
        foreach (var block in blocks)
        {
            if (block.Kind == "table")
            {
                var rows = block.Children;
                if (rows.Count == 0) continue;
                var width = rows[0].Content.GetProperty("cells").GetArrayLength();
                var separator = "| " + string.Join(" | ", Enumerable.Repeat("---", width)) + " |\n";
                var header = Flag(block.Content, "has_column_header");
                if (!header) body.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("", width))).Append(" |\n").Append(separator);
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Kind != "table_row") throw new NotionException("notion_unsupported_body");
                    Append([rows[i]], url, body, depth);
                    if (header && i == 0) body.Append(separator);
                }
                continue;
            }
            var text = Rich(block.Content);
            var line = block.Kind switch
            {
                "paragraph" => text,
                "heading_1" => "# " + text,
                "heading_2" => "## " + text,
                "heading_3" => "### " + text,
                "bulleted_list_item" => "- " + text,
                "numbered_list_item" => "1. " + text,
                "to_do" => (Flag(block.Content, "checked") ? "- [x] " : "- [ ] ") + text,
                "quote" or "callout" => "> " + text.Replace("\n", "\n> ", StringComparison.Ordinal),
                "toggle" => "- " + text,
                "divider" => "---",
                "code" => Code(block.Content),
                "image" => "![" + Escape(block.Content.TryGetProperty("caption", out var caption) ? Plain(caption) : "") + "](" + url + "#" + block.Id.Replace("-", "", StringComparison.Ordinal) + ")",
                "table" => "",
                "table_row" => "| " + string.Join(" | ", block.Content.GetProperty("cells").EnumerateArray().Select(Plain).Select(Escape)) + " |",
                _ => throw new NotionException("notion_unsupported_body")
            };
            if (line.Length > 0) body.Append(' ', depth * 2).Append(line).Append('\n');
            Append(block.Children, url, body, depth + (block.Kind == "table" ? 0 : 1));
            if (body.Length > 256 * 1024) throw new NotionException("notion_too_large");
        }
    }
    private static string Code(JsonElement content)
    {
        var text = content.TryGetProperty("rich_text", out var rich) ? Plain(rich) : "";
        var fence = Fence(text, '`', 3);
        var language = content.TryGetProperty("language", out var lang) ? lang.GetString() ?? "" : "";
        if (language.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')) language = "";
        return fence + language + "\n" + text + "\n" + fence;
    }
}
