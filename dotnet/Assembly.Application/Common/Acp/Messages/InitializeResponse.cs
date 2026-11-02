using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Acp.Messages;

/// <summary>The initialized capabilities and normalized, supported authentication descriptors.</summary>
public sealed record InitializeResponse(
    int ProtocolVersion,
    Implementation? Info,
    JsonElement Capabilities,
    IReadOnlyList<AuthMethod>? AuthMethods,
    [property: JsonPropertyName("_meta")] JsonElement? Meta)
{
    [JsonConverter(typeof(AuthMethodsConverter))]
    public IReadOnlyList<AuthMethod> AuthMethods { get; init; } = AuthMethods ?? [];

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
