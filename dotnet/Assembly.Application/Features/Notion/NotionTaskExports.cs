using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assembly.Application.Features.Notion;

public sealed partial class NotionTaskExports(NotionSettings settings, INotionDetailReader reader)
{
    [GeneratedRegex(@"\[(TSK-\d+)\]")]
    private static partial Regex TitleKey();
    public async Task<NotionTaskExport> ExportAsync(NotionTaskRef source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var connection = settings.Read();
        var normalized = new NotionTaskRef(NotionIds.Canonical(source.DataSourceId), NotionIds.Canonical(source.PageId));
        if (!connection.HasToken || connection.DataSourceId != normalized.DataSourceId) throw new NotionException("notion_source_mismatch");
        var detail = await reader.ReadAsync(normalized, ct);
        var title = ""; string? key = null;
        foreach (var p in detail.Properties.EnumerateObject())
        {
            var type = p.Value.GetProperty("type").GetString();
            if (type == "title") title = NotionBody.Plain(p.Value.GetProperty("title"));
            if (type == "unique_id")
            {
                var id = p.Value.GetProperty("unique_id");
                key = id.GetProperty("prefix").GetString() + "-" + id.GetProperty("number").GetInt64();
            }
        }
        key ??= TitleKey().Match(title) is { Success: true } match ? match.Groups[1].Value : null;
        if (string.IsNullOrEmpty(key)) throw new NotionException("missing_task_key");
        var body = NotionBody.Markdown(detail.Blocks, detail.Url);
        if (Encoding.UTF8.GetByteCount(body) > 256 * 1024) throw new NotionException("notion_too_large");
        var export = new NotionTaskExport(new("notion", normalized.DataSourceId, normalized.PageId), detail.Revision, new(key, title, body, detail.Url));
        if (JsonSerializer.SerializeToUtf8Bytes(export, NotionJson.Default.NotionTaskExport).Length > 384 * 1024) throw new NotionException("notion_too_large");
        ct.ThrowIfCancellationRequested(); settings.Check(connection.Generation);
        return export;
    }
}
