using System.Text.Json.Serialization;
using Assembly.Application.Features.Engine.Sessions;
using Assembly.Application.Features.Engine.Runs;

namespace Assembly.Application.Features.Engine.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StartSession), "start_session")]
[JsonDerivedType(typeof(SetSessionState), "set_session_state")]
[JsonDerivedType(typeof(StopSession), "stop_session")]
[JsonDerivedType(typeof(StartRun), "start_run")]
[JsonDerivedType(typeof(CancelRun), "cancel_run")]
[JsonDerivedType(typeof(StartStep), "start_step")]
[JsonDerivedType(typeof(SetStepState), "set_step_state")]
public abstract record EngineCommand;
