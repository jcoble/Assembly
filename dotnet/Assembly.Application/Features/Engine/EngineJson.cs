using Assembly.Application.Features.Engine.Reading;
using Assembly.Application.Features.Engine.Effects;
using Assembly.Application.Features.Engine.Recording;
using System.Text.Json.Serialization;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Sessions;
using Assembly.Application.Features.Engine.Runs;
using Assembly.Application.Features.Engine.NeedsMe;
using Assembly.Application.Features.Engine.Messages;
using Assembly.Application.Features.Engine.Handoffs;
using Assembly.Application.Features.Engine.Prs;

namespace Assembly.Application.Features.Engine;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
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
[JsonSerializable(typeof(MessageOutcome))]
[JsonSerializable(typeof(StartSession))]
[JsonSerializable(typeof(SetSessionState))]
[JsonSerializable(typeof(StopSession))]
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
