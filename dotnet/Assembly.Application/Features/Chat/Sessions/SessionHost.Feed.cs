using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Sessions;

public sealed partial class SessionHost
{
    // Only starts whose row/feed commit failed, retained under this existing open's gate for a possible retry.
    private readonly Dictionary<(string Acp, string Tool), (SessionObservation Observed, ToolCallChanged Start)> _failedStarts = [];

    private void PrepareObservation(RunningTurn turn, long at)
    {
        turn.Observation = _feed?.Capture(_sessionId);
        turn.StartedAt = at;
        turn.Cost = new(_converter.SessionCost);
    }

    private void BindPolicy(OpenSession session, RunningTurn turn) => session.Adapter.Link.Client.SetSessionPolicy(session.AcpSessionId,
        _feed is not null && turn.Observation is { } observed ? new FeedPolicy(this, session, observed) : this);

    private sealed class FeedPolicy(SessionHost host, OpenSession session, SessionObservation observed) : IPermissionPolicy
    {
        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken ct) => host.DecideAsync(request, ct);
        public Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, CancellationToken ct) => host.ElicitAsync(request, ct);
        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, JsonRpcRequestContext context, CancellationToken ct)
        {
            DrainBeforeAsk(ct);
            return host.DecideAsync(request, ct, observed, context, () => session.Adapter.Link.Exited.IsCompleted);
        }
        public Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, JsonRpcRequestContext context, CancellationToken ct)
        {
            DrainBeforeAsk(ct);
            return host.ElicitAsync(request, ct, observed, context, () => session.Adapter.Link.Exited.IsCompleted);
        }
        private void DrainBeforeAsk(CancellationToken ct)
        {
            lock (host._gate)
            {
                ct.ThrowIfCancellationRequested();
                if (!ReferenceEquals(host._turn?.Session ?? host._kept?.Session, session))
                    throw new OperationCanceledException("The request's parent open has closed.", ct);
                session.Adapter.Updates.Flush();
                host.Drain(session.Adapter.Updates.For(session.AcpSessionId));
            }
        }
        public void Cancelling(string actualAcpSessionId)
        {
            lock (host._gate)
            {
                if (!ReferenceEquals(host._turn?.Session ?? host._kept?.Session, session)) return;
                foreach (var ask in host._asks.Values.Where(ask => ask.AcpSessionId == actualAcpSessionId))
                    ask.Answer.TrySetCanceled();
            }
        }
        public void Decided(string method, string acpSessionId, JsonRpcRequestContext context, JsonElement outcome)
        {
            lock (host._gate)
            {
                var prepared = host._feed!.PreparePolicyAnswered(observed, context, method, outcome);
                host._hub.Publish(host._sessionId, new RowBatch(), prepared);
            }
        }
    }

    private bool CompleteFeed(IWriteScope scope, RunningTurn turn, OpenSession? session, AgentEvent end)
    {
        if (_feed is null || turn.Observation is not { } observed || turn.FeedCompleted || session is null) return false;
        var status = end is TurnFailed or AgentLost ? "failed" : end is TurnEnded { StopReason: "cancelled" } ? "cancelled" : "completed";
        var cost = turn.Cost.Delta(status, turn.TerminalObserved ? turn.TerminalCost : _converter.SessionCost);
        return _feed.Complete(scope, observed, session.AcpSessionId, new(turn.ProviderMessageId,
            turn.StartedAt, end.At, status, (end as TurnEnded)?.Usage, cost));
    }

    private sealed record TerminalTurn(RunningTurn Turn, AgentEvent End);

    private void ReplyFeed(IWriteScope scope, RunningTurn turn, OpenSession? session, AgentEvent end)
    {
        if (_feed is not null && turn.Observation is { } observed)
            _feed.Replied(scope, observed, new(turn.RunId, session?.AcpSessionId, turn.ProviderMessageId, turn.SelfStarted, end.At));
    }

    private void CommitReaderBatch(RowBatch batch, SessionObservation? observed, string acpSessionId,
        IReadOnlyList<AgentEvent> events, IReadOnlyList<TerminalTurn> terminals)
    {
        List<RunningTurn> completed = [];
        List<(RunningTurn Turn, AuthTerminalProof Proof)> proofs = [];
        List<(RunningTurn Turn, long Seq)> savedAuth = [];
        foreach (var terminal in terminals)
        {
            if (FinalAuthTerminal(terminal.Turn, terminal.Turn.Session, terminal.End) is not { } proof) continue;
            DecorateAuthTerminal(batch, proof);
            proofs.Add((terminal.Turn, proof));
        }
        var foreground = _turn is { SelfStarted: false } current && current.Session?.AcpSessionId == acpSessionId ? current : null;
        var early = foreground is null ? null : events.Select(e => CaptureAuthTerminal(foreground, acpSessionId, e)).FirstOrDefault(p => p is not null);
        var retries = _failedStarts.Where(p => p.Key.Acp == acpSessionId
            && events.OfType<ToolCallChanged>().Any(e => e.ToolCallId == p.Key.Tool)).ToArray();
        try
        {
            var hasFacts = events.Any(e => e is TurnStarted or TurnEnded or TurnFailed or AgentLost or ToolCallChanged);
            _hub.Publish(_sessionId, batch, _feed is null || observed is null || !hasFacts && terminals.Count == 0 ? null : scope =>
            {
                if (early is not null) BlockAuthTerminal(scope, foreground!, early);
                foreach (var proof in proofs)
                    savedAuth.Add((proof.Turn, ReportAuthTerminal(scope, proof.Turn, proof.Proof)));
                foreach (var retry in retries)
                    _feed.ObserveBatch(scope, retry.Value.Observed, acpSessionId, [retry.Value.Start]);
                _feed.ObserveBatch(scope, observed, acpSessionId, events);
                foreach (var terminal in terminals)
                {
                    if (CompleteFeed(scope, terminal.Turn, terminal.Turn.Session, terminal.End)) completed.Add(terminal.Turn);
                    ReplyFeed(scope, terminal.Turn, terminal.Turn.Session, terminal.End);
                }
            });
        }
        catch
        {
            if (observed is not null)
                foreach (var start in events.OfType<ToolCallChanged>().Where(e => e.Started))
                    _failedStarts.TryAdd((acpSessionId, start.ToolCallId), (observed, start));
            throw;
        }
        // These values cannot be consumed inside the observer: the outer writer can still roll back.
        foreach (var retry in retries) _failedStarts.Remove(retry.Key);
        foreach (var turn in completed) turn.FeedCompleted = true;
        foreach (var saved in savedAuth) saved.Turn.AuthTerminalSeq = saved.Seq;
        foreach (var terminal in terminals) terminal.Turn.Ended?.TrySetResult();
    }
    // Only the actual owning execution supplies Engine auth authority. Compatibility sends keep their ordinary terminal path.
    private AuthTerminalProof? CaptureAuthTerminal(RunningTurn turn, string? acpSessionId, AgentEvent end)
    {
        if (end is not TurnFailed { Code: "-32000" } || turn.Observation is not { } observed || observed.SessionId != _sessionId ||
            turn.Delivery is not { } delivery || turn.UserRow is not { Seq: > 0 } user || user.Epoch != observed.Epoch ||
            delivery.SessionId != _sessionId || delivery.Epoch != observed.Epoch || delivery.ClientId.Length == 0) return null;
        if (turn.AuthBinding is { } retained) return retained;
        int version; AuthOpenFailureStage? stage = null;
        if (acpSessionId is not null)
        {
            if (string.IsNullOrWhiteSpace(acpSessionId) || turn.Session?.AcpSessionId != acpSessionId) return null;
            version = 1;
        }
        else
        {
            if (turn.Session is not null || turn.Prompt is not null || turn.ProviderMessageId is not null ||
                turn.OpenAttempt is not { IdentityAccepted: false, Failure: { } failure } || !Enum.IsDefined(failure) ||
                !turn.Inserted.Task.IsCompletedSuccessfully || turn.Inserted.Task.Result is not SendResult.AuthRequired required ||
                required.UserRow != user) return null;
            version = 2; stage = failure;
        }
        return turn.AuthBinding = new(version, Guid.NewGuid(), Guid.NewGuid(), turn.RunId, acpSessionId, observed.Epoch,
            delivery.ClientId, null, new(delivery.MessageId, delivery.EffectId, delivery.Attempt,
                delivery.ClientId, user.Seq, user.RunId), stage);
    }

    private AuthTerminalProof? FinalAuthTerminal(RunningTurn turn, OpenSession? session, AgentEvent end)
    {
        if (turn.AuthTerminalSeq != 0) return null;
        var binding = CaptureAuthTerminal(turn, session?.AcpSessionId, end);
        if (binding is null) return null;
        var inserted = turn.ProviderMessageId ?? (turn.Prompt?.Inserted.IsCompletedSuccessfully == true ? turn.Prompt.Inserted.Result : null);
        return turn.AuthFinalProof ??= binding with { ProviderMessageId = inserted };
    }

    private static void DecorateAuthTerminal(RowBatch batch, AuthTerminalProof proof)
    {
        var index = batch.Rows.FindIndex(row => row.RunId == proof.ChatRunId && row.Kind == "turn" &&
            row.Payload.Deserialize(RowJsonContext.Rows.TurnPayload) is { State: "failed" });
        if (index < 0 || !batch.Rows.Any(row => row.RunId == proof.ChatRunId && row.Kind == "error" &&
            row.Payload.Deserialize(RowJsonContext.Rows.ErrorPayload) is { Code: "-32000" }))
            throw new CallFailed("provider_auth_terminal_missing");
        var row = batch.Rows[index];
        var payload = row.Payload.Deserialize(RowJsonContext.Rows.TurnPayload)!;
        batch.Rows[index] = row with { Payload = JsonSerializer.SerializeToElement(payload with { AuthFailure = proof }, RowJsonContext.Rows.TurnPayload) };
    }

    private void BlockAuthTerminal(IWriteScope scope, RunningTurn turn, AuthTerminalProof binding)
    {
        if (_feed is null || turn.Observation is not { } observed) return;
        if (binding.Version == 2) _feed.BlockFailedOpenSignInNeeded(scope, observed, binding);
        else _feed.BlockSignInNeeded(scope, observed, binding.AcpSessionId!, binding);
    }

    private long ReportAuthTerminal(IWriteScope scope, RunningTurn turn, AuthTerminalProof proof)
    {
        if (_feed is null || turn.Observation is not { } observed) throw new CallFailed("provider_auth_observation_missing");
        var rows = scope.Query("""
            SELECT seq,payload FROM rows WHERE session_id=$session AND run_id=$run AND kind='turn'
              AND json_extract(payload,'$.state')='failed' AND json_extract(payload,'$.authFailure.observationId')=$observation
            ORDER BY seq DESC LIMIT 2
            """, row => (Seq: row.Long(0), Payload: row.Text(1)), new SqlArg("$session", _sessionId),
            new("$run", proof.ChatRunId), new("$observation", proof.ObservationId.ToString()));
        if (rows.Count != 1 || JsonSerializer.Deserialize(rows[0].Payload, RowJsonContext.Rows.TurnPayload)?.AuthFailure != proof)
            throw new CallFailed("provider_auth_terminal_missing");
        var seq = rows[0].Seq;
        if (proof.Version == 2) _feed.FailedOpenSignInNeeded(scope, observed, seq, proof);
        else _feed.SignInNeeded(scope, observed, proof.AcpSessionId!, seq, proof);
        return seq;
    }
}
