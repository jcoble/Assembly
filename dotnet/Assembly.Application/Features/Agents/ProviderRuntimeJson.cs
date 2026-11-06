using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Agents;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ProviderRuntimeReadArgs))]
[JsonSerializable(typeof(ProviderRuntimeSaveArgs))]
[JsonSerializable(typeof(ProviderRuntimeSettings))]
[JsonSerializable(typeof(ProviderRuntimeReply))]
[JsonSerializable(typeof(ProviderRuntimeChanged))]
public sealed partial class ProviderRuntimeJson : JsonSerializerContext;
