using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// A saved row as JSON on the wire: <c>{threadId, seq, runId, itemId, kind, payload, createdAt}</c>, plus <c>firstSeq</c> on
/// page rows and <c>prevSeq</c> on live and catch-up rows.
/// </summary>
public static class RowWire
{
    /// <summary>Rows aren't escaped for HTML: the webview parses them and never pastes them into a page.</summary>
    public static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ToJson(StoredRow row)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Options))
            Write(json, row);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>A row as another machine sent it.</summary>
    public static StoredRow Read(JsonElement json) =>
        new(json.GetProperty("sessionId").GetString()!, json.GetProperty("seq").GetInt64(), json.GetProperty("runId").GetString(),
            json.GetProperty("itemId").GetString(), json.GetProperty("kind").GetString()!, json.GetProperty("payload").Clone(),
            json.GetProperty("createdAt").GetInt64())
        {
            FirstSeq = json.TryGetProperty("firstSeq", out var firstSeq) ? firstSeq.GetInt64() : null,
            PrevSeq = json.TryGetProperty("prevSeq", out var prevSeq) ? prevSeq.GetInt64() : null,
        };

    public static void Write(Utf8JsonWriter json, StoredRow row)
    {
        json.WriteStartObject();
        json.WriteString("sessionId", row.SessionId);
        json.WriteNumber("seq", row.Seq);
        json.WriteString("runId", row.RunId);
        json.WriteString("itemId", row.ItemId);
        json.WriteString("kind", row.Kind);
        json.WritePropertyName("payload");
        row.Payload.WriteTo(json);
        json.WriteNumber("createdAt", row.CreatedAt);
        if (row.FirstSeq is { } firstSeq)
            json.WriteNumber("firstSeq", firstSeq);
        if (row.PrevSeq is { } prevSeq)
            json.WriteNumber("prevSeq", prevSeq);
        json.WriteEndObject();
    }
}
