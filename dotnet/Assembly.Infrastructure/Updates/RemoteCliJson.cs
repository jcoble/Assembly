using System.Text.Json.Serialization;
using Assembly.Application.Common.Agents;

namespace Assembly.Infrastructure.Updates;

internal sealed record RemoteCliTarget(AgentDescriptor Descriptor, Dictionary<string, string> Environment,
    string ConfigurationTargetId, string AccountContext);
internal sealed record RemoteCliCaptureData(string MachineId, string InstallationId, string Home, string AgentsFile,
    string AgentsHash, AgentCommand HelperCommand, IReadOnlyList<RemoteCliTarget> Targets);

// The response never contains a private path, descriptor, environment, or raw command output.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteCliReadResult(string MachineId, string InstallationId, string ProviderId,
    string AccountContext, string ConfigurationTargetId, string? InstalledVersion, string Manager, string Source, string? Reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RemoteCliCaptureData))]
[JsonSerializable(typeof(RemoteCliReadResult))]
internal sealed partial class RemoteCliJson : JsonSerializerContext;
