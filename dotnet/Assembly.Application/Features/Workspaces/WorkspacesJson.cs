using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Workspaces;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkspaceReadArgs))]
[JsonSerializable(typeof(WorkspaceSaveArgs))]
[JsonSerializable(typeof(WorkspaceReadResult))]
[JsonSerializable(typeof(WorkspaceSaveResult))]
[JsonSerializable(typeof(WorkspaceChange))]
internal sealed partial class WorkspacesJson : JsonSerializerContext;
