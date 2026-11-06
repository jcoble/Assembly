using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Acp.Messages;

public sealed record AuthEnv(string Name, string Value);
public sealed record AuthMethod(string MethodId, string Name, string Type, string? Description,
    IReadOnlyList<string>? Args, IReadOnlyList<AuthEnv>? Env,
    [property: JsonPropertyName("_meta")] JsonElement? Meta);

/// <summary>An invalid descriptor cannot hide its valid neighbors or make initialization fail.</summary>
internal sealed class AuthMethodsConverter : JsonConverter<IReadOnlyList<AuthMethod>>
{
    public override bool HandleNull => true;
    public override IReadOnlyList<AuthMethod> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        List<AuthMethod> methods = [];
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !String(entry, "methodId", out var id)
                || !String(entry, "name", out var name) || !String(entry, "type", out var kind)
                || (kind is not ("agent" or "terminal") && !kind.StartsWith('_'))) continue;
            string? description = null;
            if (entry.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null)
            {
                if (desc.ValueKind != JsonValueKind.String) continue;
                description = desc.GetString();
            }
            List<string>? args = null;
            if (entry.TryGetProperty("args", out var arguments) && arguments.ValueKind != JsonValueKind.Null)
            {
                if (arguments.ValueKind != JsonValueKind.Array || arguments.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String)) continue;
                args = arguments.EnumerateArray().Select(a => a.GetString()!).ToList();
            }
            List<AuthEnv>? env = null;
            if (entry.TryGetProperty("env", out var environment) && environment.ValueKind != JsonValueKind.Null)
            {
                if (environment.ValueKind != JsonValueKind.Array) continue;
                env = [];
                var valid = true;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in environment.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Object || !String(value, "name", out var key) || !String(value, "value", out var text) || !names.Add(key)) { valid = false; break; }
                    env.Add(new(key, text));
                }
                if (!valid) continue;
            }
            methods.Add(new(id, name, kind, description, args, env, entry.TryGetProperty("_meta", out var meta) ? meta.Clone() : null));
        }
        return methods;
    }

    private static bool String(JsonElement element, string key, out string value)
    {
        value = "";
        if (!element.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString()!;
        return true;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<AuthMethod> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var method in value) JsonSerializer.Serialize(writer, method, AcpJsonContext.Default.AuthMethod);
        writer.WriteEndArray();
    }
}
