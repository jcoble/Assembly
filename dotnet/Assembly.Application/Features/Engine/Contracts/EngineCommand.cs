using System.Text.Json.Serialization;
using Assembly.Application.Features.Engine.Sessions;
using Assembly.Application.Features.Engine.Runs;
using Assembly.Application.Features.Engine.NeedsMe;
using Assembly.Application.Features.Engine.Messages;

namespace Assembly.Application.Features.Engine.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
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
