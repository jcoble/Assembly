using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Notion;

namespace Assembly.Infrastructure.Notion;

public sealed class NotionDetailReader(INotionHttp http) : INotionDetailReader
{
    public async Task<NotionDetail> ReadAsync(NotionTaskRef source, CancellationToken ct)
    {
        var pageId = NotionIds.Canonical(source.PageId);
        var sourceId = NotionIds.Canonical(source.DataSourceId);
        var page = await http.SendAsync(HttpMethod.Get, "/v1/pages/" + pageId, null, ct);
        if (NotionIds.Canonical(page.GetProperty("parent").GetProperty("data_source_id").GetString()!) != sourceId || NotionIds.Canonical(page.GetProperty("id").GetString()!) != pageId)
            throw new NotionException("notion_source_mismatch");
        var revision = page.GetProperty("last_edited_time").GetString()!;
        var url = SourceUrl(page.GetProperty("url").GetString()!, pageId);
        var read = new Read(http, ct);
        read.CountRaw(page);
        var blocks = await read.ChildrenAsync(pageId, 0);
        var comments = new List<JsonElement>();
        var unavailable = false;
        try
        {
            await foreach (var comment in read.PagesAsync("/v1/comments?block_id=" + pageId + "&page_size=100")) comments.Add(comment);
        }
        catch (NotionException error) when (error.Code == "notion_http_403") { unavailable = true; }
        var end = await http.SendAsync(HttpMethod.Get, "/v1/pages/" + pageId, null, ct);
        read.CountRaw(end);
        if (end.GetProperty("last_edited_time").GetString() != revision) throw new NotionException("notion_changed");
        return new(revision, page.GetProperty("properties").Clone(), blocks, comments, unavailable, url);
    }
    public static string SourceUrl(string url, string pageId)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || (uri.Host != "www.notion.so" && uri.Host != "notion.so") || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" || !uri.IsDefaultPort)
            throw new NotionException("invalid_notion_url");
        if (!uri.AbsolutePath.Replace("-", "", StringComparison.Ordinal).EndsWith(pageId.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)) throw new NotionException("invalid_notion_url");
        return "https://www.notion.so/" + pageId.Replace("-", "", StringComparison.Ordinal);
    }
    private sealed class Read(INotionHttp http, CancellationToken ct)
    {
        private readonly HashSet<string> _ids = [];
        private int _raw;
        public void CountRaw(JsonElement value)
        {
            _raw += Encoding.UTF8.GetByteCount(value.GetRawText());
            if (_raw > 8 * 1024 * 1024) throw new NotionException("notion_too_large");
        }
        public async Task<IReadOnlyList<NotionBlock>> ChildrenAsync(string id, int depth)
        {
            if (depth > 32) throw new NotionException("notion_too_large");
            var blocks = new List<NotionBlock>();
            try
            {
                await foreach (var item in PagesAsync("/v1/blocks/" + id + "/children?page_size=100"))
                {
                    var blockId = NotionIds.Canonical(item.GetProperty("id").GetString()!);
                    if (!_ids.Add(blockId)) throw new NotionException("notion_body_incomplete");
                    if (_ids.Count > 10000) throw new NotionException("notion_too_large");
                    var kind = item.GetProperty("type").GetString()!;
                    var children = item.TryGetProperty("has_children", out var nested) && nested.GetBoolean() ? await ChildrenAsync(blockId, depth + 1) : [];
                    blocks.Add(new(blockId, kind, item.TryGetProperty(kind, out var content) ? content.Clone() : JsonDocument.Parse("{}").RootElement.Clone(), children));
                }
            }
            catch (NotionException e) when (e.Code.StartsWith("notion_http_", StringComparison.Ordinal) || e.Code == "notion_transport") { throw new NotionException("notion_body_incomplete"); }
            return blocks;
        }
        public async IAsyncEnumerable<JsonElement> PagesAsync(string path)
        {
            var cursors = new HashSet<string>();
            string? cursor = null;
            do
            {
                ct.ThrowIfCancellationRequested();
                var page = await http.SendAsync(HttpMethod.Get, path + (cursor is null ? "" : "&start_cursor=" + Uri.EscapeDataString(cursor)), null, ct);
                CountRaw(page);
                if (!page.TryGetProperty("results", out var results) || !page.TryGetProperty("has_more", out var more)) throw new NotionException("notion_body_incomplete");
                foreach (var result in results.EnumerateArray()) yield return result.Clone();
                if (!more.GetBoolean()) yield break;
                cursor = page.TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
                if (string.IsNullOrEmpty(cursor) || !cursors.Add(cursor)) throw new NotionException("notion_body_incomplete");
            } while (true);
        }
    }
}
