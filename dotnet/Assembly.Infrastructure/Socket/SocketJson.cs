using System.Text.Json.Serialization;

namespace Assembly.Infrastructure.Socket;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InFrame))]
[JsonSerializable(typeof(WelcomeFrame))]
[JsonSerializable(typeof(ResultFrame))]
[JsonSerializable(typeof(ErrorFrame))]
internal sealed partial class SocketJson : JsonSerializerContext;
