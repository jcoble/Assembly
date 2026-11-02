using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;

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
                host._hub.Publish(host._sessionId, new RowBatch(), scope => host._feed!.PolicyAnswered(scope, observed, context, method, outcome));
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

    private void CommitReaderBatch(RowBatch batch, SessionObservation? observed, string acpSessionId,
        IReadOnlyList<AgentEvent> events, RunningTurn? endedTurn, AgentEvent? end)
    {
        var completed = false;
        var retries = _failedStarts.Where(p => p.Key.Acp == acpSessionId
            && events.OfType<ToolCallChanged>().Any(e => e.ToolCallId == p.Key.Tool)).ToArray();
        try
        {
            var hasFacts = events.Any(e => e is TurnStarted or TurnEnded or TurnFailed or AgentLost or ToolCallChanged);
            _hub.Publish(_sessionId, batch, _feed is null || observed is null || !hasFacts && endedTurn is null ? null : scope =>
            {
                foreach (var retry in retries)
                    _feed.ObserveBatch(scope, retry.Value.Observed, acpSessionId, [retry.Value.Start]);
                _feed.ObserveBatch(scope, observed, acpSessionId, events);
                if (endedTurn is not null && end is not null) completed = CompleteFeed(scope, endedTurn, endedTurn.Session, end);
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
        if (endedTurn is not null && completed) endedTurn.FeedCompleted = true;
    }
}
