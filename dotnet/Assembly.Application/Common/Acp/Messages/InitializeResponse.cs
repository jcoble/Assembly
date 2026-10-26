using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Acp.Messages;

/// <summary>Capabilities and auth methods stay raw until a step needs them typed.</summary>
public sealed record InitializeResponse(
    int ProtocolVersion,
    JsonElement AgentCapabilities,
    JsonElement AuthMethods,
    Implementation? AgentInfo,
    [property: JsonPropertyName("_meta")] JsonElement Meta)
{
    /// <summary>Whether the agent offers <c>session/resume</c> (<c>agentCapabilities.sessionCapabilities.resume</c>).</summary>
    [JsonIgnore]
    public bool CanResumeSessions =>
        AgentCapabilities.ValueKind == JsonValueKind.Object
        && AgentCapabilities.TryGetProperty("sessionCapabilities", out var sessions) && sessions.ValueKind == JsonValueKind.Object
        && sessions.TryGetProperty("resume", out var resume) && resume.ValueKind == JsonValueKind.Object;

    /// <summary>Whether the agent takes <c>_session/steering</c> (<c>_meta.steering.supported</c>).</summary>
    [JsonIgnore]
    public bool CanSteer =>
        Meta.ValueKind == JsonValueKind.Object
        && Meta.TryGetProperty("steering", out var steering) && steering.ValueKind == JsonValueKind.Object
        && steering.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True;

    /// <summary>Whether the agent takes <c>_session/rename</c> (<c>_meta.jetbrains.air.capabilities</c> lists <c>sessionRename</c>).</summary>
    [JsonIgnore]
    public bool CanRenameSessions =>
        Meta.ValueKind == JsonValueKind.Object
        && Meta.TryGetProperty("jetbrains", out var jetbrains) && jetbrains.ValueKind == JsonValueKind.Object
        && jetbrains.TryGetProperty("air", out var air) && air.ValueKind == JsonValueKind.Object
        && air.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array
        && capabilities.EnumerateArray().Any(capability => capability.ValueKind == JsonValueKind.String && capability.GetString() == "sessionRename");
}
