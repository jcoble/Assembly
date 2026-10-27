using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Diagnostics;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MemoryReading))]
[JsonSerializable(typeof(SoakLine))]
[JsonSerializable(typeof(Empty))]
internal sealed partial class DiagnosticsJson : JsonSerializerContext;
