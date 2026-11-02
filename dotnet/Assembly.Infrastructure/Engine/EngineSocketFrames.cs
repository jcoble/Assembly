using System.Text.Json;
using Assembly.Application.Common.Engine;
using System.Text.Json.Serialization;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Engine.Recording;
using Assembly.Engine.Reading;
using Assembly.Engine.Relays;

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
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(MachineStartAckArgs))]
[JsonSerializable(typeof(MachineStartAck))]
[JsonSerializable(typeof(ForeignTitleSourceOpenArgs))]
[JsonSerializable(typeof(ForeignTitleSourceOpened))]
[JsonSerializable(typeof(ForeignTitleSourceChecked))]
[JsonSerializable(typeof(ForeignTitlePartArgs))]
[JsonSerializable(typeof(ForeignTitlePart))]
[JsonSerializable(typeof(ForeignAncestryProofQuery))]
[JsonSerializable(typeof(ForeignRegisterReceiptQuery))]
[JsonSerializable(typeof(ForeignTitleRequest))]
[JsonSerializable(typeof(ForeignTitleAppendArgs))]
[JsonSerializable(typeof(ForeignTitleOffsets))]
[JsonSerializable(typeof(ForeignTitleCommitArgs))]
[JsonSerializable(typeof(ForeignTitleReply))]
[JsonSerializable(typeof(RelaySourceReadArgs))]
[JsonSerializable(typeof(RelaySourceOpened))]
[JsonSerializable(typeof(RelaySourcePartArgs))]
[JsonSerializable(typeof(RelaySourcePart))]
[JsonSerializable(typeof(RelaySourceDescriptor))]
[JsonSerializable(typeof(RelayHandleArgs))]
[JsonSerializable(typeof(RelaySendRequest))]
[JsonSerializable(typeof(RelaySendBeginReply))]
[JsonSerializable(typeof(RelaySendPartArgs))]
[JsonSerializable(typeof(RelaySendPartAccepted))]
[JsonSerializable(typeof(RelaySendCommitArgs))]
[JsonSerializable(typeof(RelaySendAck))]
[JsonSerializable(typeof(RelayOutcomeArgs))]
[JsonSerializable(typeof(SessionTitleReadArgs))]
[JsonSerializable(typeof(SessionTitleStat))]
[JsonSerializable(typeof(SessionTitleChunkArgs))]
[JsonSerializable(typeof(SessionTitleChunk))]
[JsonSerializable(typeof(OwnerPromptManifest))]
[JsonSerializable(typeof(OwnerPromptReadArgs))]
[JsonSerializable(typeof(OwnerPromptPage))]
[JsonSerializable(typeof(OwnerPromptChunkArgs))]
[JsonSerializable(typeof(OwnerPromptChunk))]
[JsonSerializable(typeof(MachineInfo))]
[JsonSerializable(typeof(WindowCommand))]
[JsonSerializable(typeof(MachineCommand))]
[JsonSerializable(typeof(ForeignAncestryArgs))]
[JsonSerializable(typeof(StartLocationArgs))]
[JsonSerializable(typeof(StartLocation))]
[JsonSerializable(typeof(EngineUsageArgs))]
[JsonSerializable(typeof(EngineEntityArgs))]
[JsonSerializable(typeof(UsageTotals))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(EngineSnapshotFrame))]
[JsonSerializable(typeof(EngineEventsFrame))]
internal sealed partial class EngineSocketJson : JsonSerializerContext;
