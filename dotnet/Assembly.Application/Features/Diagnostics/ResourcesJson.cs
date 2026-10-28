using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Diagnostics;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ResourceUsage))]
internal sealed partial class ResourcesJson : JsonSerializerContext;
