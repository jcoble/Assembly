using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Acp.Messages;

/// <summary>Capabilities and auth methods stay raw until a step needs them typed.</summary>
public sealed record InitializeResponse(
    int ProtocolVersion,
    Implementation? Info,
    JsonElement Capabilities,
    JsonElement? AuthMethods,
    [property: JsonPropertyName("_meta")] JsonElement? Meta)
{
    /// <summary>Whether the agent takes <c>_session/steering</c> (<c>_meta.steering.supported</c>).</summary>
    [JsonIgnore]
    public bool CanSteer =>
        Meta is { ValueKind: JsonValueKind.Object } meta
        && meta.TryGetProperty("steering", out var steering) && steering.ValueKind == JsonValueKind.Object
        && steering.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True;

    [JsonIgnore]
    public bool CanDelete => Capabilities.ValueKind == JsonValueKind.Object
        && Capabilities.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object
        && session.TryGetProperty("delete", out var delete) && delete.ValueKind == JsonValueKind.Object;
}
