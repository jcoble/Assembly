using System.Text.Json;
using Assembly.Application.Common.Engine;
using System.Text.Json.Serialization;
using Assembly.Engine.Contracts;
using Assembly.Engine.Recording;
using Assembly.Engine.Reading;

namespace Assembly.Infrastructure.Engine;

internal sealed record EngineSnapshotFrame(long Id, string Machine, long Through, IReadOnlyList<JsonElement> Items, bool More,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CursorAt? At = null)
{
    [JsonPropertyOrder(-1)] public string T => "engine_snapshot";
}

internal sealed record EngineEventsFrame(long Id, string Machine, long After, long Through, IReadOnlyList<EngineEvent> Events, bool More,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<JsonElement>? Rows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CursorAt? At = null)
{
    [JsonPropertyOrder(-1)] public string T => "engine_events";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(OwnerPromptManifest))]
[JsonSerializable(typeof(OwnerPromptReadArgs))]
[JsonSerializable(typeof(OwnerPromptPage))]
[JsonSerializable(typeof(OwnerPromptChunkArgs))]
[JsonSerializable(typeof(OwnerPromptChunk))]
[JsonSerializable(typeof(MachineInfo))]
[JsonSerializable(typeof(WindowCommand))]
[JsonSerializable(typeof(EngineUsageArgs))]
[JsonSerializable(typeof(EngineEntityArgs))]
[JsonSerializable(typeof(UsageTotals))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(EngineSnapshotFrame))]
[JsonSerializable(typeof(EngineEventsFrame))]
internal sealed partial class EngineSocketJson : JsonSerializerContext;
