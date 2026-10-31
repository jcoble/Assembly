using System.Text.Json.Serialization;
using Assembly.Application.Features.Engine.Sessions;

namespace Assembly.Application.Features.Engine.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StartSession), "start_session")]
[JsonDerivedType(typeof(SetSessionState), "set_session_state")]
[JsonDerivedType(typeof(StopSession), "stop_session")]
public abstract record EngineCommand;
