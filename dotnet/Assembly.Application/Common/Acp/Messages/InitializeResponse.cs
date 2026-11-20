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

    /// <summary>
    /// How a message reaches a running turn: <c>_session/steering</c> when <c>_meta.steering.supported</c>, else a
    /// <c>session/prompt</c> sent mid-turn when <c>capabilities._meta.claudeCode.promptQueueing</c>, else not at all.
    /// </summary>
    [JsonIgnore]
    public SteerRoute Steering =>
        IsTrue(Meta, "steering", "supported") ? SteerRoute.SteeringCall
        : IsTrue(Capabilities, "_meta", "claudeCode", "promptQueueing") ? SteerRoute.PromptQueue
        : SteerRoute.None;

    [JsonIgnore]
    public bool CanSteer => Steering != SteerRoute.None;

    [JsonIgnore]
    public bool CanDelete => Capabilities.ValueKind == JsonValueKind.Object
        && Capabilities.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object
        && session.TryGetProperty("delete", out var delete) && delete.ValueKind == JsonValueKind.Object;

    private static bool IsTrue(JsonElement? root, params string[] path)
    {
        if (root is not { } node) return false;
        foreach (var name in path)
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node)) return false;
        return node.ValueKind == JsonValueKind.True;
    }
}
