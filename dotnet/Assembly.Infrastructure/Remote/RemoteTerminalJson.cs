using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Infrastructure.Remote;

internal sealed record RemoteTerminalFrame(string T, long? Id = null, string? Token = null, int? Protocol = null,
    string? Feature = null, string? Method = null, JsonElement? Args = null, string? Topic = null, string? Key = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RemoteTerminalFrame))]
internal sealed partial class RemoteTerminalJson : JsonSerializerContext;
