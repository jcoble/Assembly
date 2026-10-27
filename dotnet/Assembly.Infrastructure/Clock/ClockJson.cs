using System.Text.Json.Serialization;

namespace Assembly.Infrastructure.Clock;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClockTime))]
internal sealed partial class ClockJson : JsonSerializerContext;
