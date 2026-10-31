using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Recording;

namespace Assembly.Infrastructure.Engine;

internal sealed record EngineSnapshotFrame(long Id, string Machine, long Through, IReadOnlyList<JsonElement> Items, bool More)
{
    [JsonPropertyOrder(-1)] public string T => "engine_snapshot";
}

internal sealed record EngineEventsFrame(long Id, string Machine, long After, long Through, IReadOnlyList<EngineEvent> Events, bool More)
{
    [JsonPropertyOrder(-1)] public string T => "engine_events";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(WindowCommand))]
[JsonSerializable(typeof(EngineUsageArgs))]
[JsonSerializable(typeof(EngineEntityArgs))]
[JsonSerializable(typeof(UsageTotals))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(EngineSnapshotFrame))]
[JsonSerializable(typeof(EngineEventsFrame))]
internal sealed partial class EngineSocketJson : JsonSerializerContext;
