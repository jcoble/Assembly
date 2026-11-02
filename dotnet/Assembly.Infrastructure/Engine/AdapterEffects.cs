using Assembly.Application.Common.Engine;
using Assembly.Infrastructure.Engine.Auth;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;
using Assembly.Domain.Rows;

namespace Assembly.Infrastructure.Engine;

/// <summary>Executes actual owning host operations; durable completion remains inside the Engine writer.</summary>
public sealed partial class AdapterEffects(IAppDatabase db, SessionRegistry hosts, SessionStore sessions, RowStore rows) : IEngineEffects
{
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement((string?)null, global::Assembly.Engine.EngineJson.Default.String);
    private static EffectReceipt Receipt(EffectDisposition disposition, string? error = null) => new(disposition, Empty, error);
    private static EffectReceipt SessionReceipt() => new(EffectDisposition.Confirmed,
        JsonSerializer.SerializeToElement(new SessionEffectData("idle", null, null), global::Assembly.Engine.EngineJson.Default.SessionEffectData), null);
    private static EffectReceipt MessageReceipt(string state, string? providerId, string? reason, UserRowReference? userRow = null, AuthRetryExecution? retry = null) =>
        new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new MessageEffectData(state, providerId, reason, userRow, retry),
            global::Assembly.Engine.EngineJson.Default.MessageEffectData), null);

    public async Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (effect.Kind == "handoff") return Receipt(EffectDisposition.Unavailable, "handoff_not_available");
        try
        {
            using var scope = db.OpenSnapshot(ct);
            if (!Owns(scope, effect)) return Receipt(EffectDisposition.Unknown, "delivery_owner_changed");
            switch (effect.Kind)
            {
                case "start":
                    return sessions.Get(scope, effect.SessionId!) is null
                    ? Receipt(EffectDisposition.Failed, "session_projection_missing") : SessionReceipt();
                case "stop":
                    await hosts.For(effect.SessionId!).StopCurrentAsync(ct);
                    return SessionReceipt();
                case "answer": return ExecuteAnswer(scope, effect);
                case "send":
                    var delivery = MessageDelivery.Read(scope, effect);
                    if (delivery is null) return Receipt(EffectDisposition.Unknown, "delivery_owner_changed");
                    var prior = Positive(scope, effect, delivery);
                    if (prior is not null) return prior;
                    var host = hosts.For(effect.SessionId!);
                    var admission = Admission(scope, effect, delivery);
                    if (delivery.Intent.How == "steer")
                    {
                        var steered = await host.SteerDeliveryAsync(delivery.Intent.Text, delivery.Intent.ClientId, delivery.Label, admission, ct, delivery.FromSessionId);
                        return steered.Outcome == SteerResult.Injected ? MessageReceipt("delivered_to_turn", null, null, steered.UserRow) : MessageReceipt("queued", null, "prompt_required");
                    }
                    var sent = await host.SendAsync(delivery.Intent.Text, delivery.Intent.ClientId, delivery.Label, admission, ct, delivery.FromSessionId, delivery.Intent.AttachmentIds ?? []);
                    return sent switch
                    {
                        SendResult.Inserted inserted => MessageReceipt("next_turn", inserted.ProviderMessageId, null, inserted.UserRow, inserted.RetryExecution),
                        SendResult.AuthRequired auth => MessageReceipt("queued", null, "auth_required", auth.UserRow, auth.RetryExecution),
                        SendResult.Busy => MessageReceipt("queued", null, "busy"),
                        SendResult.Failed failed => MessageReceipt("failed", null, failed.Reason),
                        _ => Receipt(EffectDisposition.Unknown, "delivery_unknown")
                    };
                case "handoff": return Receipt(EffectDisposition.Unavailable, "handoff_not_available");
                default: return Receipt(EffectDisposition.Unavailable, "effect_not_available");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return Receipt(EffectDisposition.Unknown, error.Message); }
    }

    public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (effect.Kind == "handoff") return Task.FromResult(Receipt(EffectDisposition.Unavailable, "handoff_not_available"));
        try
        {
            using var scope = db.OpenSnapshot(ct);
            if (!Owns(scope, effect, allowDone: true)) return Task.FromResult(Receipt(EffectDisposition.Unknown, "delivery_owner_changed"));
            var result = effect.Kind switch
            {
                "send" => MessageDelivery.Read(scope, effect, allowDone: true) is { } delivery ? ReconcileAuthDelivery(scope, effect, delivery) : Receipt(EffectDisposition.Unknown, "delivery_owner_changed"),
                "start" => sessions.Get(scope, effect.SessionId!) is null ? Receipt(EffectDisposition.Failed, "session_projection_missing") : SessionReceipt(),
                "stop" => rows.OpenRun(effect.SessionId!) is null ? SessionReceipt() : Receipt(EffectDisposition.Unknown),
                "answer" => ReconcileAnswer(scope, effect),
                "handoff" => Receipt(EffectDisposition.Unavailable, "handoff_not_available"),
                _ => Receipt(EffectDisposition.Unknown)
            };
            return Task.FromResult(result);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { return Task.FromResult(Receipt(EffectDisposition.Unknown, error.Message)); }
    }

    private bool Owns(IReadScope scope, Effect effect, bool allowDone = false)
    {
        var chat = effect.SessionId is null ? null : sessions.Get(scope, effect.SessionId);
        if (chat is not null && (!string.IsNullOrEmpty(chat.Machine) ||
            !chat.Helper && chat.ParentSessionId is { } parent && sessions.Get(scope, parent) is not null)) return false;
        if (chat is null && effect.Kind != "start") return false;
        return scope.QueryFirst("""
        SELECT o.attempt,o.payload_json,o.state FROM engine_outbox o JOIN engine_session s ON s.id=o.session_id
        JOIN engine_meta meta ON meta.singleton=1 AND o.machine=meta.machine_id AND s.machine=meta.machine_id
        WHERE o.id=$id AND o.session_id=$session AND s.holder='local' AND s.state NOT IN ('foreign','handing_off')
        """, r => r.Long(0) == effect.Attempt && r.Text(1) == effect.Payload.GetRawText() &&
            (r.Text(2) == "inflight" || allowDone && r.Text(2) == "done"), new SqlArg("$id", effect.Id), new("$session", effect.SessionId));
    }

    private sealed record AnswerDelivery(NeedAnswer Intent, string Chat, string State,
        string? RunId, string? StepId, JsonElement? Answer, long Epoch, int? Attempt, bool Reserved, bool Ended);

    private static AnswerDelivery? Answer(IReadScope scope, Effect effect)
    {
        var intent = effect.Payload.Deserialize(global::Assembly.Engine.EngineJson.Default.NeedAnswer);
        if (intent is null || effect.Id != "answer/" + intent.NeedId) return null;
        return scope.QueryFirst("""
            SELECT n.chat_row_id,n.state,n.run_id,n.step_id,n.answer_json,s.epoch,st.attempt,s.reserved_epoch,s.state
            FROM engine_needs_me n JOIN engine_session s ON s.id=n.session_id
            LEFT JOIN engine_step st ON st.id=n.step_id AND st.session_id=n.session_id AND st.run_id=n.run_id
            WHERE n.id=$id AND n.session_id=$session AND n.provider_request_id=$request
                AND n.machine=s.machine AND n.run_id IS $run
            """, r => r.IsNull(0) || string.IsNullOrWhiteSpace(r.Text(0)) ? null : new AnswerDelivery(intent, r.Text(0), r.Text(1),
                r.IsNull(2) ? null : r.Text(2), r.IsNull(3) ? null : r.Text(3),
                r.IsNull(4) ? null : JsonDocument.Parse(r.Text(4)).RootElement.Clone(), r.Long(5),
                r.IsNull(6) ? null : checked((int)r.Long(6)), !r.IsNull(7), r.Text(8) == "ended"),
            new SqlArg("$id", intent.NeedId), new("$session", effect.SessionId),
            new("$request", intent.ProviderRequestId), new("$run", effect.RunId));
    }

    private EffectReceipt ExecuteAnswer(IReadScope scope, Effect effect)
    {
        var answer = Answer(scope, effect);
        if (answer is null) return Receipt(EffectDisposition.Unknown, "delivery_owner_changed");
        var positive = PositiveAnswer(scope, effect, answer);
        if (positive is not null) return positive;
        if (answer.Intent.Binding is null && answer.Intent.Cancellation is null)
            return Receipt(EffectDisposition.Unknown);
        if (!MatchesBinding(effect, answer) || answer.Ended)
            return Receipt(EffectDisposition.Unknown, "delivery_owner_changed");
        var host = hosts.For(effect.SessionId!);
        var accepted = answer.Intent.Binding is { } binding
            ? host.AnswerProviderRequest(binding, answer.Intent.Answer)
            : host.CancelProviderRequest(answer.Intent.ProviderRequestId, answer.Intent.Cancellation!);
        return accepted ? AnswerReceipt(answer.Intent) : Receipt(EffectDisposition.Failed, "request_gone");
    }

    private static bool MatchesBinding(Effect effect, AnswerDelivery delivery)
    {
        var intent = delivery.Intent;
        if (delivery.Reserved || intent.Binding is not null && intent.Cancellation is not null) return false;
        if (intent.Binding is { Observation: { } observed } binding)
            return delivery.State == "answered" && delivery.Answer is { } stored &&
                JsonElement.DeepEquals(stored, intent.Answer) && binding.SessionId == effect.SessionId &&
                binding.ChatRowId == delivery.Chat && binding.ProviderRequestId == intent.ProviderRequestId &&
                observed.SessionId == effect.SessionId && observed.Epoch == delivery.Epoch &&
                observed.RunId == delivery.RunId && observed.StepId == delivery.StepId && observed.Attempt == delivery.Attempt;
        if (intent.Cancellation is { } cancellation)
            return intent.Answer.ValueKind == JsonValueKind.Null && delivery.Answer is null &&
                cancellation.Reason is "dismissed" or "expired" or "cancelled" && delivery.State == cancellation.Reason &&
                cancellation.SessionId == effect.SessionId && cancellation.ChatRowId == delivery.Chat &&
                cancellation.Epoch == delivery.Epoch && cancellation.RunId == delivery.RunId &&
                cancellation.StepId == delivery.StepId && cancellation.Attempt == delivery.Attempt;
        return false;
    }

    private static EffectReceipt? PositiveAnswer(IReadScope scope, Effect effect, AnswerDelivery answer)
    {
        var legacy = answer.Intent.Binding is null && answer.Intent.Cancellation is null;
        if (!legacy && !MatchesBinding(effect, answer)) return null;
        var settled = scope.QueryFirst("""
            SELECT payload FROM rows WHERE session_id=$session AND item_id=$item AND kind='ask' ORDER BY seq DESC LIMIT 1
            """, r => JsonDocument.Parse(r.Text(0)).RootElement.Clone(), new SqlArg("$session", effect.SessionId), new("$item", "ask:" + answer.Chat));
        if (settled.ValueKind != JsonValueKind.Object || !settled.TryGetProperty("state", out var state)) return null;
        var cancelled = answer.Intent.Cancellation is not null;
        if (cancelled && state.ValueKind == JsonValueKind.String && state.GetString() == "cancelled" ||
            !cancelled && state.ValueKind == JsonValueKind.String && state.GetString() == "answered" &&
            settled.TryGetProperty("answer", out var actual) && JsonElement.DeepEquals(actual, answer.Intent.Answer))
            return AnswerReceipt(answer.Intent);
        return null;
    }

    private static EffectReceipt AnswerReceipt(NeedAnswer answer) => new(EffectDisposition.Confirmed,
        JsonSerializer.SerializeToElement(new AnswerEffectData(answer.ProviderRequestId), global::Assembly.Engine.EngineJson.Default.AnswerEffectData), null);

    private static EffectReceipt ReconcileAnswer(IReadScope scope, Effect effect) =>
        Answer(scope, effect) is { } answer ? PositiveAnswer(scope, effect, answer) ?? Receipt(EffectDisposition.Unknown)
            : Receipt(EffectDisposition.Unknown);
}
