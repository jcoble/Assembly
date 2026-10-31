using Assembly.Application.Features.Engine.Recording;
using System.Text.Json.Serialization;
using Assembly.Application.Features.Engine.Sessions;
using Assembly.Application.Features.Engine.Runs;
using Assembly.Application.Features.Engine.NeedsMe;
using Assembly.Application.Features.Engine.Messages;
using Assembly.Application.Features.Engine.Handoffs;
using Assembly.Application.Features.Engine.Prs;

namespace Assembly.Application.Features.Engine.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ToolStarted), "tool_started")]
[JsonDerivedType(typeof(ToolEnded), "tool_ended")]
[JsonDerivedType(typeof(TurnUsage), "turn_usage")]
[JsonDerivedType(typeof(ReportPr), "report_pr")]
[JsonDerivedType(typeof(ObservePr), "observe_pr")]
[JsonDerivedType(typeof(RequestHandoff), "request_handoff")]
[JsonDerivedType(typeof(HandoffOutcome), "handoff_outcome")]
[JsonDerivedType(typeof(SendMessage), "send_message")]
[JsonDerivedType(typeof(MessageOutcome), "message_outcome")]
[JsonDerivedType(typeof(StartSession), "start_session")]
[JsonDerivedType(typeof(SetSessionState), "set_session_state")]
[JsonDerivedType(typeof(StopSession), "stop_session")]
[JsonDerivedType(typeof(StartRun), "start_run")]
[JsonDerivedType(typeof(CancelRun), "cancel_run")]
[JsonDerivedType(typeof(StartStep), "start_step")]
[JsonDerivedType(typeof(SetStepState), "set_step_state")]
[JsonDerivedType(typeof(OpenNeed), "open_need")]
[JsonDerivedType(typeof(ResolveNeed), "resolve_need")]
public abstract record EngineCommand;
