using System.Text.Json.Serialization;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Sessions;
using Assembly.Application.Features.Engine.Runs;

namespace Assembly.Application.Features.Engine;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandContext))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(EngineCommand))]
[JsonSerializable(typeof(StartSession))]
[JsonSerializable(typeof(SetSessionState))]
[JsonSerializable(typeof(StopSession))]
[JsonSerializable(typeof(StartRun))]
[JsonSerializable(typeof(CancelRun))]
[JsonSerializable(typeof(StartStep))]
[JsonSerializable(typeof(SetStepState))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(EngineEvent))]
[JsonSerializable(typeof(EngineEventData))]
public sealed partial class EngineJson : JsonSerializerContext;
