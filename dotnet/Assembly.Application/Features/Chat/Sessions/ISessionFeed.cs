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
    Action<IWriteScope> PrepareAsked(SessionObservation observed, string askId, AskPayload card, JsonRpcRequestContext request);
    Action<IWriteScope> PrepareSettled(SessionObservation observed, string askId, string how, JsonElement? answer, string reason);
    Action<IWriteScope> PreparePolicyAnswered(SessionObservation observed, JsonRpcRequestContext request, string method, JsonElement outcome);
    void Connected(IWriteScope scope, SessionObservation observed, string acpSessionId, bool canSteer);
    void Lost(IWriteScope scope, SessionObservation observed, string reason);
    void BlockSignInNeeded(IWriteScope scope, SessionObservation observed, string acpSessionId, AuthTerminalProof binding);
    void SignInNeeded(IWriteScope scope, SessionObservation observed, string acpSessionId, long terminalSeq, AuthTerminalProof proof);
    void BlockFailedOpenSignInNeeded(IWriteScope scope, SessionObservation observed, AuthTerminalProof binding);
    void FailedOpenSignInNeeded(IWriteScope scope, SessionObservation observed, long terminalSeq, AuthTerminalProof proof);
    void RetryStarted(IWriteScope scope, SessionObservation observed, AuthRetryExecution execution);
    void Replied(IWriteScope scope, SessionObservation observed, SessionTurnReply final);
    void Recovered(IWriteScope scope, RecoveredChatTurn recovered);
    /// Positive owning-execution completion after its original task and adapter ownership release.
    void Released(SessionObservation observed, string chatRunId);
    bool Complete(IWriteScope scope, SessionObservation observed, string acpSessionId, SessionTurnFinalization final);
}

public sealed record SessionTurnReply(string ChatRunId, string? AcpSessionId,
    string? ProviderMessageId, bool SelfStarted, long EndedAt);
