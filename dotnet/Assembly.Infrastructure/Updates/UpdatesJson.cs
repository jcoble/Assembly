using System.Text.Json.Serialization;

namespace Assembly.Infrastructure.Updates;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CliCheckArgs([property: JsonRequired] string MachineId, [property: JsonRequired] string ProviderId,
    [property: JsonRequired] string AccountContext, [property: JsonRequired] string ConfigurationTargetId);

internal sealed record CliCheckResult(string MachineId, string ProviderId, string AccountContext, string ConfigurationTargetId,
    string? InstalledVersion, string Manager, string Source, CliCandidate? Candidate, string Comparison, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InstalledArgs;

/// <summary>False when one was already running, so this click joined it.</summary>
internal sealed record UpdateStarted(bool Started);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CliCheckArgs))]
[JsonSerializable(typeof(CliCheckResult))]
[JsonSerializable(typeof(InstalledArgs))]
[JsonSerializable(typeof(InstalledTools))]
[JsonSerializable(typeof(UpdateStarted))]
internal sealed partial class UpdatesJson : JsonSerializerContext;
