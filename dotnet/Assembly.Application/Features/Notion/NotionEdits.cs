using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Notion;

public sealed record NotionEditReceipt(string PageId, string Revision, string State, string? Error);
public sealed record NotionPropertyArgs(NotionTaskRef Source, string Property, string Value, string ObservedRevision);
public sealed record NotionBlockArgs(NotionTaskRef Source, string BlockId, string Kind, string Text, bool? Checked, string ObservedRevision);
public sealed record NotionAppendArgs(NotionTaskRef Source, string Markdown);
public sealed record NotionPreparedEdit(NotionEditReceipt Receipt, Action<IWriteScope>? Projection = null)
{
    public NotionEditReceipt Commit(IWriteScope scope) { Projection?.Invoke(scope); return Receipt; }
}

/// <summary>Preflight is best effort; Notion has no revision CAS. Never retries an external mutation.</summary>
public sealed class NotionEdits(IAppDatabase db, NotionSettings settings, INotionHttp http)
{
    public async Task<NotionEditReceipt> PropertyAsync(NotionTaskRef source, string property, string value, string observedRevision, CancellationToken ct)
    {
        var pending = await PreparePropertyAsync(source, property, value, observedRevision, ct);
        try { return db.Writer.Write("notion.property", pending.Commit); }
        catch (Exception e) when (e is not OperationCanceledException && pending.Receipt.State == "confirmed") { return pending.Receipt with { State = "cache_stale", Error = "cache_stale" }; }
    }
    public async Task<NotionPreparedEdit> PreparePropertyAsync(NotionTaskRef source, string property, string value, string observedRevision, CancellationToken ct)
    {
        source = Normalize(source);
        if (property is not ("title" or "Status" or "Priority")) throw new NotionException("unsupported_property");
        if (Encoding.UTF8.GetByteCount(value) > 8192) throw new NotionException("notion_too_large");
        var generation = settings.Read().Generation;
        var page = await Preflight(source, observedRevision, ct);
        var properties = page.GetProperty("properties");
        var name = property;
        if (property == "title") name = properties.EnumerateObject().FirstOrDefault(p => p.Value.GetProperty("type").GetString() == "title").Name ?? throw new NotionException("unsupported_property");
        if (!properties.TryGetProperty(name, out var field)) throw new NotionException("unsupported_property");
        var kind = field.GetProperty("type").GetString()!;
        if (kind is not ("title" or "select" or "status")) throw new NotionException("unsupported_property");
        var body = NotionPayload.Build(w =>
        {
            w.WritePropertyName("properties"); w.WriteStartObject(); w.WritePropertyName(name); w.WriteStartObject();
            w.WritePropertyName(kind);
            if (kind == "title") Rich(w, value);
            else if (value.Length == 0) w.WriteNullValue();
            else { w.WriteStartObject(); w.WriteString("name", value); w.WriteEndObject(); }
            w.WriteEndObject(); w.WriteEndObject();
        });
        var receipt = await Mutate(source.PageId, "/v1/pages/" + NotionIds.Canonical(source.PageId), body, observedRevision, ct);
        if (receipt.State != "confirmed") return new(receipt);
        return new(receipt, scope =>
        {
            var current = scope.QueryFirst("SELECT generation FROM notion_connection WHERE singleton=1", r => r.Long(0));
            if (current != generation) throw new NotionException("notion_connection_changed");
            var column = property switch { "title" => "title", "Status" => "status", _ => "priority" };
            scope.Execute($"UPDATE notion_tasks SET {column}=$v WHERE source_id=$s AND page_id=$p", new("$v", value), new("$s", source.DataSourceId), new("$p", source.PageId));
            scope.Execute("UPDATE notion_cache_meta SET revision=revision+1 WHERE source_id=$s", new SqlArg("$s", source.DataSourceId));
            NotionCache.Publish(scope, source.DataSourceId, generation);
        });
    }
    public async Task<NotionEditReceipt> BlockAsync(NotionTaskRef source, string blockId, string kind, string text, bool? check, string observedRevision, CancellationToken ct)
    {
        source = Normalize(source);
        var notionKind = kind switch { "paragraph" => "paragraph", "heading1" => "heading_1", "heading2" => "heading_2", "heading3" => "heading_3", "bullet" => "bulleted_list_item", "numbered" => "numbered_list_item", "todo" => "to_do", "quote" => "quote", "code" => "code", _ => throw new NotionException("notion_unsupported_body") };
        ValidateText(text);
        if (JsonSerializer.SerializeToUtf8Bytes(new NotionBlockArgs(source, blockId, kind, text, check, observedRevision), NotionJson.Default.NotionBlockArgs).Length > 256 * 1024 - 1024) throw new NotionException("notion_too_large");
        await Preflight(source, observedRevision, ct);
        var id = NotionIds.Canonical(blockId);
        var block = await http.SendAsync(HttpMethod.Get, "/v1/blocks/" + id, null, ct);
        if (block.GetProperty("type").GetString() != notionKind) throw new NotionException("notion_unsupported_body");
        var parent = block.GetProperty("parent");
        var visited = new HashSet<string> { id };
        while (parent.TryGetProperty("block_id", out var ancestor))
        {
            var next = NotionIds.Canonical(ancestor.GetString()!);
            if (visited.Count >= 32 || !visited.Add(next)) throw new NotionException("notion_source_mismatch");
            parent = (await http.SendAsync(HttpMethod.Get, "/v1/blocks/" + next, null, ct)).GetProperty("parent");
        }
        if (!parent.TryGetProperty("page_id", out var owner) || NotionIds.Canonical(owner.GetString()!) != NotionIds.Canonical(source.PageId)) throw new NotionException("notion_source_mismatch");
        var payload = NotionPayload.Build(w =>
        {
            w.WritePropertyName(notionKind); w.WriteStartObject(); w.WritePropertyName("rich_text"); Rich(w, text);
            if (notionKind == "to_do" && check is { } c) w.WriteBoolean("checked", c);
            w.WriteEndObject();
        });
        return await Mutate(source.PageId, "/v1/blocks/" + id, payload, observedRevision, ct);
    }
    public async Task<NotionEditReceipt> AppendTextAsync(NotionTaskRef source, string markdown, CancellationToken ct)
    {
        source = Normalize(source);
        ValidateText(markdown);
        if (JsonSerializer.SerializeToUtf8Bytes(new NotionAppendArgs(source, markdown), NotionJson.Default.NotionAppendArgs).Length > 256 * 1024 - 1024) throw new NotionException("notion_too_large");
        var page = await Preflight(source, null, ct);
        var revision = page.GetProperty("last_edited_time").GetString()!;
        var lines = markdown.Split('\n');
        if (lines.Length > 100) throw new NotionException("notion_too_large");
        var body = NotionPayload.Build(w =>
        {
            w.WritePropertyName("children"); w.WriteStartArray();
            foreach (var line in lines)
            {
                w.WriteStartObject(); w.WriteString("object", "block"); w.WriteString("type", "paragraph");
                w.WritePropertyName("paragraph"); w.WriteStartObject(); w.WritePropertyName("rich_text"); Rich(w, line); w.WriteEndObject(); w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        return await Mutate(source.PageId, "/v1/blocks/" + NotionIds.Canonical(source.PageId) + "/children", body, revision, ct, lines.Length);
    }
    public async Task<JsonElement> Preflight(NotionTaskRef source, string? observedRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var connection = settings.Read();
        if (NotionIds.Canonical(source.DataSourceId) != connection.DataSourceId || !connection.HasToken) throw new NotionException("notion_source_mismatch");
        var page = await http.SendAsync(HttpMethod.Get, "/v1/pages/" + NotionIds.Canonical(source.PageId), null, ct);
        if (NotionIds.Canonical(page.GetProperty("parent").GetProperty("data_source_id").GetString()!) != connection.DataSourceId || NotionIds.Canonical(page.GetProperty("id").GetString()!) != NotionIds.Canonical(source.PageId)) throw new NotionException("notion_source_mismatch");
        settings.Check(connection.Generation);
        if (observedRevision is not null && page.GetProperty("last_edited_time").GetString() != observedRevision) throw new NotionException("notion_changed");
        return page;
    }
    public async Task<NotionEditReceipt> Mutate(string pageId, string path, JsonElement body, string revision, CancellationToken ct, int? expectedChildren = null)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var result = await http.SendAsync(HttpMethod.Patch, path, body, ct);
            if (expectedChildren is { } expected && (!result.TryGetProperty("results", out var children) || children.GetArrayLength() != expected)) return new(pageId, revision, "uncertain", "notion_partial_acknowledgment");
            if (expectedChildren is null && (!result.TryGetProperty("id", out var ackId) || !Guid.TryParse(ackId.GetString(), out var actualId) || !Guid.TryParse(path.Split('/')[^1], out var targetId) || actualId != targetId || !result.TryGetProperty("last_edited_time", out _))) return new(pageId, revision, "uncertain", "notion_invalid_acknowledgment");
            if (result.TryGetProperty("last_edited_time", out var updated)) revision = updated.GetString()!;
            return new(pageId, revision, "confirmed", null);
        }
        catch (Exception e) when (e is OperationCanceledException || e is NotionException { Code: "notion_transport" or "notion_invalid_response" or "notion_connection_changed" } || e is NotionException server && server.Code.StartsWith("notion_http_5", StringComparison.Ordinal))
        { return new(pageId, revision, "uncertain", "notion_outcome_unknown"); }
    }
    private static NotionTaskRef Normalize(NotionTaskRef source) => new(NotionIds.Canonical(source.DataSourceId), NotionIds.Canonical(source.PageId));
    private static void ValidateText(string text) { if (Encoding.UTF8.GetByteCount(text) > 128 * 1024) throw new NotionException("notion_too_large"); }
    private static void Rich(Utf8JsonWriter w, string text)
    {
        w.WriteStartArray();
        // Notion limits each rich-text content item to 2000 UTF-16 code units; preserve surrogate pairs.
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(2000, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
            w.WriteStartObject(); w.WriteString("type", "text"); w.WritePropertyName("text"); w.WriteStartObject(); w.WriteString("content", text.Substring(offset, length)); w.WriteEndObject(); w.WriteEndObject();
            offset += length;
        }
        w.WriteEndArray();
    }
}
