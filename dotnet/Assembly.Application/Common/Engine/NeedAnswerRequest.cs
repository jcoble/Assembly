using System.Text.Json;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Application.Common.Engine;

public sealed record NeedAnswerRequest(string Session, string Ask, JsonElement Answer);

public sealed record ProviderAnswerBinding(string SessionId, string ChatRowId,
    string ProviderRequestId, SessionObservation Observation, string AcpSessionId);

public sealed record PreparedProviderAnswer(ProviderAnswerBinding Binding, JsonElement Answer);

public sealed record ProviderTerminalBinding(string SessionId, string ChatRowId,
    long Epoch, string? RunId, string? StepId, int? Attempt, string Reason);
