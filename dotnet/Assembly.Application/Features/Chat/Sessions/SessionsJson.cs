using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Chat.Sessions;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionItem))]
[JsonSerializable(typeof(SessionsFeature.PageArgs))]
[JsonSerializable(typeof(SessionsFeature.Page))]
[JsonSerializable(typeof(SessionsFeature.CreateArgs))]
[JsonSerializable(typeof(SessionsFeature.Created))]
[JsonSerializable(typeof(SendFeature.SendArgs))]
[JsonSerializable(typeof(SendFeature.Sent))]
[JsonSerializable(typeof(SendFeature.StopArgs))]
[JsonSerializable(typeof(SendFeature.AnswerArgs))]
[JsonSerializable(typeof(SendFeature.StopTaskArgs))]
[JsonSerializable(typeof(SendFeature.Done))]
internal sealed partial class SessionsJson : JsonSerializerContext;
