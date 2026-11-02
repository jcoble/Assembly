using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Providers;

public abstract record ProviderCommand : EngineCommand;
public sealed record CheckProvider() : ProviderCommand;
public sealed record RecordProviderCheck(string Installation, string AuthState, JsonElement Facts,
    string? Reason, ProviderProbeBinding Probe) : ProviderCommand;
public sealed record InterruptProviderCheck() : ProviderCommand;
public sealed record ProviderProbeBinding(string TargetId, string EffectId, long ProviderVersion, int ClaimAttempt);
public sealed record ProbeProviderEffect(string TargetId, long ProviderVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PriorAuthState = null);
public sealed record ProviderEffectData(string TargetId, long ProviderVersion);
