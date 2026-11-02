using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Recording;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

/// <summary>Translates owning provider facts inside the chat writer; owns no adapter or background work.</summary>
public sealed class EngineFeed(EngineRuntime runtime, TextWriter log) : ISessionFeed
{
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(null, global::Assembly.Engine.EngineJson.Default.String);

    public SessionObservation? Capture(string sessionId)
    {
        var observed = ((IEngineRuntime)runtime).Read(scope => scope.QueryFirst("""
            SELECT s.id,COALESCE(st.run_id,s.run_id),st.id,st.attempt,s.epoch
            FROM engine_session s LEFT JOIN engine_step st ON st.id=(
                SELECT id FROM engine_step WHERE session_id=s.id AND state IN ('running','waiting')
                ORDER BY updated_ms DESC,id LIMIT 1)
            WHERE s.id=$id AND s.machine=$machine AND s.state NOT IN ('foreign','ended')
            """, r => new SessionObservation(r.Text(0), Text(r, 1), Text(r, 2),
                r.IsNull(3) ? null : checked((int)r.Long(3)), r.Long(4)),
            new SqlArg("$id", sessionId), new SqlArg("$machine", runtime.Machine)), CancellationToken.None);
        if (observed is null) Diagnostic("owning_session_missing");
        return observed;
    }

    public void ObserveBatch(IWriteScope scope, SessionObservation observed, string acpSessionId, IReadOnlyList<AgentEvent> events)
    {
        foreach (var e in events)
            switch (e)
            {
                case TurnStarted: State(scope, observed, "running", acpSessionId, null); break;
                case TurnEnded: State(scope, observed, "idle", acpSessionId, null); break;
                case TurnFailed failed: State(scope, observed, "idle", acpSessionId, failed.Code ?? "provider_failed"); break;
                case AgentLost: Lost(scope, observed, "provider_gone"); break;
                case ToolCallChanged tool: Tool(scope, observed, acpSessionId, tool); break;
            }
    }

    public void Connected(IWriteScope scope, SessionObservation observed, string acpSessionId, bool canSteer)
    {
        Apply(scope, observed, "session", observed.SessionId, new ReportCapabilities(canSteer));
        if (CurrentState(scope, observed.SessionId) == "lost") State(scope, observed, "idle", acpSessionId, null);
    }

    public void Lost(IWriteScope scope, SessionObservation observed, string reason) => State(scope, observed, "lost", null, reason);

    private void State(IWriteScope scope, SessionObservation observed, string state, string? acpSessionId, string? reason)
    {
        var current = scope.QueryFirst("SELECT state,activity_state FROM engine_session WHERE id=$id AND machine=$machine",
            r => (State: r.Text(0), Activity: r.Text(1)), new SqlArg("$id", observed.SessionId), new SqlArg("$machine", runtime.Machine));
        if (current.State == state || current.State == "needs_me" && current.Activity == state) return;
        Apply(scope, observed, "session", observed.SessionId, new SetSessionState(state, acpSessionId, reason));
    }

    public void Asked(IWriteScope scope, SessionObservation observed, string askId, AskPayload card, JsonRpcRequestContext request) =>
        Apply(scope, observed, "needs_me", askId, new OpenNeed(observed.SessionId, observed.RunId, observed.StepId,
            card.Kind, string.IsNullOrWhiteSpace(card.Message) ? card.Title ?? "" : card.Message,
            card.Options?.Select(o => o.OptionId).ToArray() ?? [], request.ProviderRequestId, askId, null));

    public void Settled(IWriteScope scope, SessionObservation observed, string askId, string how, JsonElement? answer, string reason)
    {
        var state = scope.QueryFirst("SELECT state FROM engine_needs_me WHERE id=$id AND session_id=$session AND machine=$machine",
            r => r.Text(0), new SqlArg("$id", askId), new SqlArg("$session", observed.SessionId), new SqlArg("$machine", runtime.Machine));
        if (state is null || state != "open") return;
        Apply(scope, observed, "needs_me", askId, new ResolveNeed(how, answer, reason));
    }

    public void PolicyAnswered(IWriteScope scope, SessionObservation observed, JsonRpcRequestContext request, string method, JsonElement outcome)
    {
        var id = scope.QueryFirst("""
            SELECT id FROM engine_needs_me WHERE session_id=$session AND machine=$machine AND provider_request_id=$request
            """, r => r.Text(0), new SqlArg("$session", observed.SessionId), new SqlArg("$machine", runtime.Machine),
            new SqlArg("$request", request.ProviderRequestId));
        if (id is not null) Settled(scope, observed, id, "answered", outcome, "policy_answered");
    }

    private void Tool(IWriteScope scope, SessionObservation observed, string acpSessionId, ToolCallChanged change)
    {
        if (change.Fields.AcpSessionId != acpSessionId) { Diagnostic("tool_session_mismatch"); return; }
        var boundary = acpSessionId + "/" + change.ToolCallId;
        var original = scope.QueryFirst("""
            SELECT id,session_id,run_id,step_id,attempt,epoch,ended_ms FROM engine_tool_call
            WHERE machine=$machine AND session_id=$session AND boundary='acp' AND boundary_id=$boundary
            """, r => new
        {
            Id = r.Text(0),
            A = new Attribution(r.Text(1), Text(r, 2), Text(r, 3),
                r.IsNull(4) ? null : checked((int)r.Long(4)), r.Long(5)),
            Ended = !r.IsNull(6)
        },
            new SqlArg("$machine", runtime.Machine), new SqlArg("$session", observed.SessionId), new SqlArg("$boundary", boundary));
        if (original is null)
        {
            // Started means first observed by UpdateReader; a bare terminal patch is not an original start.
            if (!change.Started || string.IsNullOrWhiteSpace(change.Fields.Name))
            { Diagnostic("tool_start_provenance_missing"); return; }
            var input = change.Fields.Input ?? Empty;
            var id = Guid.NewGuid().ToString("N");
            var start = new ToolStarted(AttributionOf(observed), "acp", boundary,
                change.Fields.Name, input, Bytes(input));
            var result = Apply(scope, observed, "tool_call", id, start);
            if (!result.Applied) return;
            original = new { Id = id, A = start.Attribution, Ended = false };
        }
        if (original.Ended || change.Fields.Status is not ("completed" or "failed" or "cancelled")) return;
        var output = JsonSerializer.SerializeToElement(change.Fields.Output, global::Assembly.Engine.EngineJson.Default.String);
        var originalObservation = new SessionObservation(original.A.SessionId, original.A.RunId, original.A.StepId, original.A.Attempt, original.A.Epoch);
        Apply(scope, originalObservation, "tool_call", original.Id,
            new ToolEnded(original.A, change.Fields.Status, output, change.Fields.Output is { } text ? Encoding.UTF8.GetByteCount(text) : 0));
    }

    public bool Complete(IWriteScope scope, SessionObservation observed, string acpSessionId, SessionTurnFinalization final)
    {
        if (string.IsNullOrWhiteSpace(final.ProviderMessageId)) { Diagnostic("provider_message_id_missing"); return false; }
        try
        {
            var u = final.Usage;
            if (u?.InputTokens < 0 || u?.OutputTokens < 0 || u?.CachedReadTokens < 0 || u?.CachedWriteTokens < 0 || final.CostUsd < 0)
                throw new OverflowException();
            long? input = u is { InputTokens: { } uncached, CachedReadTokens: { } read, CachedWriteTokens: { } write }
                ? checked(uncached + read + write) : null;
            long? cost = final.CostUsd is { } usd
                ? checked((long)decimal.Round(usd * 1_000_000_000m, 0, MidpointRounding.ToEven)) : null;
            var duration = checked(Math.Max(final.EndedAt, final.StartedAt) - final.StartedAt);
            return Apply(scope, observed, "usage", Guid.NewGuid().ToString("N"), new TurnUsage(AttributionOf(observed),
                final.ProviderMessageId, input, u?.OutputTokens, u?.CachedReadTokens, cost, duration)).Applied;
        }
        catch (OverflowException) { Diagnostic("invalid_usage_metric"); return false; }
    }

    private CommandResult Apply(IWriteScope scope, SessionObservation observed, string kind, string id, EngineCommand command)
    {
        var result = runtime.SubmitCurrent(scope, kind, id, command, observed.Epoch);
        if (!result.Applied) Diagnostic(result.Error ?? "fact_refused");
        return result;
    }
    private string? CurrentState(IReadScope scope, string id) => scope.QueryFirst(
        "SELECT state FROM engine_session WHERE id=$id AND machine=$machine", r => r.Text(0),
        new SqlArg("$id", id), new SqlArg("$machine", runtime.Machine));
    private static Attribution AttributionOf(SessionObservation observed) =>
        new(observed.SessionId, observed.RunId, observed.StepId, observed.Attempt, observed.Epoch);
    private static string? Text(IRow r, int column) => r.IsNull(column) ? null : r.Text(column);
    private static long Bytes(JsonElement value) => Encoding.UTF8.GetByteCount(value.GetRawText());
    private void Diagnostic(string code) => log.WriteLine($"engine feed: {code}");
}
