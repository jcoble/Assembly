using System.Text.Json.Serialization;

namespace Assembly.Infrastructure.Updates;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CliCheckArgs([property: JsonRequired] string MachineId, [property: JsonRequired] string ProviderId,
    [property: JsonRequired] string AccountContext, [property: JsonRequired] string ConfigurationTargetId);

internal sealed record CliCheckResult(string MachineId, string ProviderId, string AccountContext, string ConfigurationTargetId,
    string? InstalledVersion, string Manager, string Source, CliCandidate? Candidate, string Comparison, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InstalledArgs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InstallArgs([property: JsonRequired] string Machine);

/// <summary>False when one was already running, so this click joined it.</summary>
internal sealed record UpdateStarted(bool Started);

/// <summary>The check finished. What it found goes out on the machines topic, like every other check's.</summary>
internal sealed record UpdateChecked(bool Checked);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CliCheckArgs))]
[JsonSerializable(typeof(CliCheckResult))]
[JsonSerializable(typeof(InstalledArgs))]
[JsonSerializable(typeof(InstallArgs))]
[JsonSerializable(typeof(InstalledTools))]
[JsonSerializable(typeof(UpdateStarted))]
[JsonSerializable(typeof(UpdateChecked))]
internal sealed partial class UpdatesJson : JsonSerializerContext;
