using System.Text.Json.Serialization;

namespace Assembly.Infrastructure.Updates;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CliCheckArgs([property: JsonRequired] string MachineId, [property: JsonRequired] string ProviderId,
    [property: JsonRequired] string AccountContext, [property: JsonRequired] string ConfigurationTargetId);

internal sealed record CliCheckResult(string MachineId, string ProviderId, string AccountContext, string ConfigurationTargetId,
    string? InstalledVersion, string Manager, string Source, CliCandidate? Candidate, string Comparison, string? Reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CliCheckArgs))]
[JsonSerializable(typeof(CliCheckResult))]
internal sealed partial class UpdatesJson : JsonSerializerContext;
