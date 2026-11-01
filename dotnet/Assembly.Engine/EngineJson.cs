using Assembly.Engine.Reading;
using Assembly.Engine.Mirror;
using Assembly.Engine.Effects;
using Assembly.Engine.Recording;
using System.Text.Json.Serialization;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Engine.Runs;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Messages;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Prs;

namespace Assembly.Engine;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MirrorNotice))]
[JsonSerializable(typeof(CursorAt))]
[JsonSerializable(typeof(StreamPage))]
[JsonSerializable(typeof(SnapshotPage))]
[JsonSerializable(typeof(Effect))]
[JsonSerializable(typeof(EffectReceipt))]
[JsonSerializable(typeof(ClaimEffect))]
[JsonSerializable(typeof(RecoverEffect))]
[JsonSerializable(typeof(EnableEffect))]
[JsonSerializable(typeof(CompleteEffect))]
[JsonSerializable(typeof(SessionEffectData))]
[JsonSerializable(typeof(AnswerEffectData))]
[JsonSerializable(typeof(MessageEffectData))]
[JsonSerializable(typeof(HandoffEffectData))]
[JsonSerializable(typeof(CommandContext))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(EngineCommand))]
[JsonSerializable(typeof(ToolStarted))]
[JsonSerializable(typeof(ToolEnded))]
[JsonSerializable(typeof(TurnUsage))]
[JsonSerializable(typeof(ReportPr))]
[JsonSerializable(typeof(ObservePr))]
[JsonSerializable(typeof(RequestHandoff))]
[JsonSerializable(typeof(HandoffOutcome))]
[JsonSerializable(typeof(HandoffTransfer))]
[JsonSerializable(typeof(SendMessage))]
[JsonSerializable(typeof(DeliverMessage))]
[JsonSerializable(typeof(MessageOutcome))]
[JsonSerializable(typeof(StartSession))]
[JsonSerializable(typeof(SetSessionState))]
[JsonSerializable(typeof(ReportCapabilities))]
[JsonSerializable(typeof(StopSession))]
[JsonSerializable(typeof(LinkTask))]
[JsonSerializable(typeof(StartRun))]
[JsonSerializable(typeof(CancelRun))]
[JsonSerializable(typeof(StartStep))]
[JsonSerializable(typeof(SetStepState))]
[JsonSerializable(typeof(OpenNeed))]
[JsonSerializable(typeof(ResolveNeed))]
[JsonSerializable(typeof(NeedAnswer))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(EngineEvent))]
[JsonSerializable(typeof(EngineEventData))]
public sealed partial class EngineJson : JsonSerializerContext;
