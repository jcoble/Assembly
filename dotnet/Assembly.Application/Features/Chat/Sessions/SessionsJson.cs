using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Chat.Sessions;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionItem))]
[JsonSerializable(typeof(SessionsFeature.PageArgs))]
[JsonSerializable(typeof(SessionsFeature.Page))]
[JsonSerializable(typeof(SessionsFeature.CreateArgs))]
[JsonSerializable(typeof(SessionsFeature.Created))]
internal sealed partial class SessionsJson : JsonSerializerContext;
