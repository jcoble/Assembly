using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Application.Common.Engine;

public sealed record NeedAnswerRequest(string Session, string Ask, JsonElement Answer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpectedSource? ExpectedSource = null);

public sealed record ProviderAnswerBinding(string SessionId, string ChatRowId,
    string ProviderRequestId, SessionObservation Observation, string AcpSessionId);

public sealed record PreparedProviderAnswer(ProviderAnswerBinding Binding, JsonElement Answer);

public sealed record ProviderTerminalBinding(string SessionId, string ChatRowId,
    long Epoch, string? RunId, string? StepId, int? Attempt, string Reason);
