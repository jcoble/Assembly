using System.Text.Json.Serialization;
using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandContext))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(EngineCommand))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(EngineEvent))]
[JsonSerializable(typeof(EngineEventData))]
public sealed partial class EngineJson : JsonSerializerContext;
