using System.Text.Json.Serialization;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Common.Acp;

/// <summary>Compile-time JSON metadata for every ACP message we send or read. No reflection, so it stays AOT-safe.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InitializeRequest))]
[JsonSerializable(typeof(InitializeResponse))]
[JsonSerializable(typeof(NewSessionRequest))]
[JsonSerializable(typeof(NewSessionResponse))]
[JsonSerializable(typeof(ResumeSessionRequest))]
[JsonSerializable(typeof(ResumeSessionResponse))]
[JsonSerializable(typeof(CloseSessionRequest))]
[JsonSerializable(typeof(CloseSessionResponse))]
[JsonSerializable(typeof(SetConfigOptionRequest))]
[JsonSerializable(typeof(SetConfigOptionResponse))]
[JsonSerializable(typeof(PromptRequest))]
[JsonSerializable(typeof(PromptResponse))]
[JsonSerializable(typeof(CancelNotification))]
[JsonSerializable(typeof(RenameSessionRequest))]
[JsonSerializable(typeof(RenameSessionResponse))]
[JsonSerializable(typeof(SteerRequest))]
[JsonSerializable(typeof(SteerResponse))]
[JsonSerializable(typeof(StopTaskRequest))]
[JsonSerializable(typeof(StopTaskResponse))]
[JsonSerializable(typeof(SessionNotification))]
[JsonSerializable(typeof(RequestPermissionRequest))]
[JsonSerializable(typeof(RequestPermissionResponse))]
[JsonSerializable(typeof(ElicitationRequest))]
[JsonSerializable(typeof(ElicitationResponse))]
internal sealed partial class AcpJsonContext : JsonSerializerContext;
