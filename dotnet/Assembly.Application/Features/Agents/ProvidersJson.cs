using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Agents;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProviderInputReply))]
[JsonSerializable(typeof(ChallengeArgs))]
[JsonSerializable(typeof(SubmitCodeArgs))]
[JsonSerializable(typeof(AcknowledgeUrlArgs))]
[JsonSerializable(typeof(ChallengeReply))]
[JsonSerializable(typeof(ProviderFacts))]
[JsonSerializable(typeof(ProviderTarget))]
[JsonSerializable(typeof(ModelChoice))]
[JsonSerializable(typeof(SignInChoice))]
public sealed partial class ProvidersJson : JsonSerializerContext;
