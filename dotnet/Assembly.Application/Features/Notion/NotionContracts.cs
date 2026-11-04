using System.Text.Json;

namespace Assembly.Application.Features.Notion;

public sealed class NotionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

// Infrastructure supplies the HTTP boundary; Application never depends on an infrastructure assembly.
public interface INotionHttp
{
    Task<JsonElement> SendWithTokenAsync(HttpMethod method, string path, JsonElement? body, byte[] token, CancellationToken ct);
    Task<JsonElement> SendAsync(HttpMethod method, string path, JsonElement? body, CancellationToken ct);
}
public sealed record NotionSettingsView(string DataSourceId, bool HasToken, long Generation, string State, string? Error);
public sealed record NotionOAuthStart(string OperationId, string AuthorizationUrl, string State);
public sealed record NotionEmpty;
public sealed record NotionSaveSettings(string? DataSourceId, string? Token);
public sealed record NotionClaim(string OperationId, string State, string ClaimCode);
public sealed record NotionOperation(string OperationId);
public sealed record NotionRevision(long Generation, long Revision);

public static class NotionIds
{
    public static string Canonical(string id) => Guid.TryParseExact(id, "N", out var n) || Guid.TryParseExact(id, "D", out n)
        ? n.ToString("D") : throw new NotionException("invalid_notion_id");
}

public static class NotionPayload
{
    public static JsonElement Build(Action<Utf8JsonWriter> fields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) { writer.WriteStartObject(); fields(writer); writer.WriteEndObject(); }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}

public sealed record NotionImageBegin(NotionTaskRef Source, string Mime, long Size);
public sealed record NotionImageChunk(string UploadId, long Offset, string Data);
public sealed record NotionImageOperation(string UploadId);
public sealed record NotionImageLease(string UploadId, int ChunkBytes);
