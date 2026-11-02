using System.Text.Json.Serialization;
using Assembly.Engine.Recording;

namespace Assembly.Engine.Sessions;

/// <summary>Capabilities and accepted identity observed from the owning adapter, never a user setting.</summary>
public sealed record ReportCapabilities(bool CanSteer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderSessionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Attribution? Observation = null) : SessionCommand;
