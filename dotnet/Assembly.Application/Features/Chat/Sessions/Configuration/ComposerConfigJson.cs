using System.Text.Json;
using Assembly.Domain.Sessions;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Chat.Sessions.Configuration;

/// <summary>The public three-field config shape, with raw per-model snapshots only on discovery.</summary>
public static class ComposerConfigJson
{
    public static JsonElement Json(SessionConfig config, IReadOnlyDictionary<string, string>? models = null)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) Write(json, config, models);
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    public static void Write(Utf8JsonWriter json, SessionConfig config, IReadOnlyDictionary<string, string>? models = null)
    {
        json.WriteStartObject();
        json.WritePropertyName("options");
        if (config.Options is { } options) json.WriteRawValue(options); else json.WriteNullValue();
        json.WriteStartObject("choices");
        foreach (var (id, value) in config.Choices) json.WriteString(id, value);
        json.WriteEndObject();
        json.WritePropertyName("commands");
        if (config.Commands is { } commands) json.WriteRawValue(commands); else json.WriteNullValue();
        if (models is not null)
        {
            json.WriteStartObject("modelOptions");
            foreach (var (model, snapshot) in models) { json.WritePropertyName(model); json.WriteRawValue(snapshot); }
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }

    public static InitialSessionConfig? Initial(JsonElement body)
    {
        if (!body.TryGetProperty("initialConfig", out var initial) || initial.ValueKind == JsonValueKind.Null) return null;
        if (initial.ValueKind != JsonValueKind.Object || !initial.TryGetProperty("targetId", out var target) || target.ValueKind != JsonValueKind.String
            || !initial.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Object
            || choices.EnumerateObject().Any(x => x.Value.ValueKind != JsonValueKind.String))
            throw new CallFailed("Expected initialConfig {targetId, choices}.");
        return new InitialSessionConfig(target.GetString()!, choices.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!));
    }

}
