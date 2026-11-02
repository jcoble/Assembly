using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Database;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Application.Features.Chat.Sessions;

public sealed record SessionObservation(string SessionId, string? RunId, string? StepId, int? Attempt, long Epoch);
public sealed record SessionTurnFinalization(string? ProviderMessageId, long StartedAt, long EndedAt,
    string Status, TokenUsage? Usage, decimal? CostUsd);

/// <summary>Owning provider facts, observed in the same write as their chat rows.</summary>
public interface ISessionFeed
{
    SessionObservation? Capture(string sessionId);
    void ObserveBatch(IWriteScope scope, SessionObservation observed, string acpSessionId, IReadOnlyList<AgentEvent> events);
    void Asked(IWriteScope scope, SessionObservation observed, string askId, AskPayload card, JsonRpcRequestContext request);
    void Settled(IWriteScope scope, SessionObservation observed, string askId, string how, JsonElement? answer, string reason);
    void PolicyAnswered(IWriteScope scope, SessionObservation observed, JsonRpcRequestContext request, string method, JsonElement outcome);
    void Connected(IWriteScope scope, SessionObservation observed, string acpSessionId, bool canSteer);
    void Lost(IWriteScope scope, SessionObservation observed, string reason);
    void Replied(IWriteScope scope, SessionObservation observed, SessionTurnReply final);
    void Recovered(IWriteScope scope, RecoveredChatTurn recovered);
    bool Complete(IWriteScope scope, SessionObservation observed, string acpSessionId, SessionTurnFinalization final);
}

public sealed record SessionTurnReply(string ChatRunId, string? AcpSessionId,
    string? ProviderMessageId, bool SelfStarted, long EndedAt);
