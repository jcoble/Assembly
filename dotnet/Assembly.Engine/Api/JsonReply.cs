using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Assembly.Engine.Store;

namespace Assembly.Engine.Api;

/// <summary>JSON answers, written straight with <see cref="Utf8JsonWriter"/>, so nothing needs reflection.</summary>
internal static class JsonReply
{
    public static async Task WriteAsync(HttpContext context, int status, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, RowWire.Options))
            body(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        // A length, not chunks: the app's image upload reads a plain HTTP/1.0 answer.
        context.Response.ContentLength = buffer.WrittenCount;
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted);
    }

    public static Task ErrorAsync(HttpContext context, int status, string message) =>
        WriteAsync(context, status, json =>
        {
            json.WriteStartObject();
            json.WriteString("error", message);
            json.WriteEndObject();
        });

    /// <summary>The request's JSON object, or null if the body isn't one.</summary>
    public static async Task<JsonElement?> ReadObjectAsync(HttpContext context)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? String(JsonElement body, string property) =>
        body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
