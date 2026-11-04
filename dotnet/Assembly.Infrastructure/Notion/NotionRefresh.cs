using System.Text.Json;
using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Notion;

namespace Assembly.Infrastructure.Notion;

public sealed class NotionRefresh(INotionHttp http, NotionCache cache, NotionSettings settings, TimeProvider time)
{
    private const int MaxProjectReads = 50;
    public async Task<NotionRefreshReceipt> RefreshAsync(long expectedGeneration, CancellationToken ct)
    {
        var prepared = await PrepareAsync(expectedGeneration, ct);
        return cache.Commit(expectedGeneration, prepared.Rows, prepared.FetchedMs, prepared.Truncated);
    }
    public sealed record Prepared(IReadOnlyList<NotionTaskRow> Rows, long FetchedMs, bool Truncated);
    public async Task<Prepared> PrepareAsync(long expectedGeneration, CancellationToken ct)
    {
        settings.Check(expectedGeneration);
        var connection = settings.Read();
        if (!connection.HasToken) throw new NotionException("missing_token");
        var rows = new List<NotionTaskRow>(); var cursors = new HashSet<string>();
        string? cursor = null;
        var fetched = time.GetUtcNow().ToUnixTimeMilliseconds();
        var total = 0;
        do
        {
            ct.ThrowIfCancellationRequested(); settings.Check(expectedGeneration);
            var body = NotionPayload.Build(w => { w.WriteNumber("page_size", 100); Newest(w); if (cursor is not null) w.WriteString("start_cursor", cursor); });
            var response = await http.SendAsync(HttpMethod.Post, "/v1/data_sources/" + connection.DataSourceId + "/query", body, ct);
            total += Encoding.UTF8.GetByteCount(response.GetRawText());
            if (total > 8 * 1024 * 1024) throw new NotionException("notion_too_large");
            foreach (var page in response.GetProperty("results").EnumerateArray())
            {
                if (rows.Count == 500) { settings.Check(expectedGeneration); ct.ThrowIfCancellationRequested(); return new(await NamedAsync(rows, ct), fetched, true); }
                if (NotionIds.Canonical(page.GetProperty("parent").GetProperty("data_source_id").GetString()!) != connection.DataSourceId) throw new NotionException("notion_source_mismatch");
                rows.Add(Parse(page, connection.DataSourceId, fetched, ct));
            }
            var more = response.GetProperty("has_more").GetBoolean();
            if (rows.Count == 500) { settings.Check(expectedGeneration); return new(await NamedAsync(rows, ct), fetched, more); }
            if (!more) break;
            cursor = response.TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor) || !cursors.Add(cursor)) throw new NotionException("notion_body_incomplete");
        } while (true);
        ct.ThrowIfCancellationRequested(); settings.Check(expectedGeneration);
        return new(await NamedAsync(rows, ct), fetched, false);
    }
    // The cap keeps the newest tasks, whatever order the source's own view has.
    private static void Newest(System.Text.Json.Utf8JsonWriter w)
    {
        w.WritePropertyName("sorts"); w.WriteStartArray(); w.WriteStartObject();
        w.WriteString("timestamp", "created_time"); w.WriteString("direction", "descending");
        w.WriteEndObject(); w.WriteEndArray();
    }
    // A relation holds only page ids, so each related project is read once for its title. A project the connection
    // can't read leaves its tasks with no project rather than failing the list. Rows without an explicit map carry the
    // relation's id as their project id.
    private async Task<List<NotionTaskRow>> NamedAsync(List<NotionTaskRow> rows, CancellationToken ct)
    {
        var titles = new Dictionary<string, string>();
        var named = new List<NotionTaskRow>(rows.Count);
        foreach (var row in rows)
        {
            if (row.ProjectTitle.Length > 0 || row.ProjectId.Length == 0) { named.Add(row); continue; }
            if (!titles.TryGetValue(row.ProjectId, out var title))
            {
                title = titles.Count < MaxProjectReads ? await ProjectTitleAsync(row.ProjectId, ct) : "";
                titles[row.ProjectId] = title;
            }
            named.Add(title.Length > 0 ? row with { ProjectTitle = title } : row with { ProjectId = "" });
        }
        return named;
    }
    private async Task<string> ProjectTitleAsync(string id, CancellationToken ct)
    {
        try
        {
            var page = await http.SendAsync(HttpMethod.Get, "/v1/pages/" + id, null, ct);
            foreach (var p in page.GetProperty("properties").EnumerateObject())
                if (p.Value.GetProperty("type").GetString() == "title") return NotionBody.Plain(p.Value.GetProperty("title"));
            return "";
        }
        catch (NotionException e) when (e.Code is "notion_http_403" or "notion_http_404") { return ""; }
    }
    private NotionTaskRow Parse(JsonElement page, string source, long fetched, CancellationToken ct)
    {
        var id = NotionIds.Canonical(page.GetProperty("id").GetString()!);
        string title = "", key = "", status = "", priority = "", assignee = "";
        string? due = null; long number = 0; (string Id, string Title) project = ("", "");
        foreach (var p in page.GetProperty("properties").EnumerateObject())
        {
            var type = p.Value.GetProperty("type").GetString()!;
            var value = p.Value.GetProperty(type);
            if (type == "title") title = NotionBody.Plain(value);
            if (type == "unique_id") { number = value.GetProperty("number").GetInt64(); key = value.GetProperty("prefix").GetString() + "-" + number; }
            if (p.Name == "Status" && type is "status" or "select") status = Name(value);
            if (p.Name == "Priority" && type == "select") priority = Name(value);
            if (p.Name == "Assignee" && type == "people") assignee = string.Join(", ", value.EnumerateArray().Select(Name));
            if (p.Name == "Due" && type == "date" && value.ValueKind != JsonValueKind.Null) due = value.GetProperty("start").GetString();
            if (p.Name == "Project" && type == "relation")
            {
                foreach (var relation in value.EnumerateArray())
                {
                    var mapped = cache.Project(source, NotionIds.Canonical(relation.GetProperty("id").GetString()!), ct);
                    if (mapped.Id is not null) { project = mapped; break; }
                    if (project.Id.Length == 0) project = (NotionIds.Canonical(relation.GetProperty("id").GetString()!), "");
                }
            }
        }
        if (title.Length > 8192) throw new NotionException("notion_too_large");
        return new(source, id, title, number, key, project.Id, project.Title, status, priority, assignee, due,
            NotionDetailReader.SourceUrl(page.GetProperty("url").GetString()!, id), fetched);
    }
    private static string Name(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
}
