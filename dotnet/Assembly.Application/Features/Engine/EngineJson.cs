using System.Text.Json.Serialization;
using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EngineEvent))]
[JsonSerializable(typeof(EngineEventData))]
public sealed partial class EngineJson : JsonSerializerContext;
