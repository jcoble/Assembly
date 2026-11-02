using Assembly.Application.Common.Engine;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Effects;
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
    private static EffectReceipt MessageReceipt(string state, string? providerId, string? reason, UserRowReference? userRow = null) =>
        new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new MessageEffectData(state, providerId, reason, userRow),
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
                case "answer":
                    var answer = Answer(scope, effect);
                    if (answer is null || !hosts.For(effect.SessionId!).AnswerProviderRequest(answer.Value.Chat, answer.Value.Intent.ProviderRequestId, answer.Value.Intent.Answer))
                        return Receipt(EffectDisposition.Failed, "request_gone");
                    return new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new AnswerEffectData(answer.Value.Intent.ProviderRequestId), global::Assembly.Engine.EngineJson.Default.AnswerEffectData), null);
                case "send":
                    var delivery = Delivery(scope, effect);
                    if (delivery is null) return Receipt(EffectDisposition.Unknown, "delivery_owner_changed");
                    var prior = Positive(scope, delivery);
                    if (prior is not null) return prior;
                    var host = hosts.For(effect.SessionId!);
                    var admission = Admission(scope, effect, delivery);
                    if (delivery.Intent.How == "steer")
                    {
                        var steered = await host.SteerDeliveryAsync(delivery.Intent.Text, delivery.Intent.ClientId, delivery.Label, admission, ct, delivery.FromSessionId);
                        return steered.Outcome == SteerResult.Injected ? MessageReceipt("delivered_to_turn", null, null, steered.UserRow) : MessageReceipt("queued", null, "prompt_required");
                    }
                    var sent = await host.SendAsync(delivery.Intent.Text, delivery.Intent.ClientId, delivery.Label, admission, ct, delivery.FromSessionId);
                    return sent switch
                    {
                        SendResult.Inserted inserted => MessageReceipt("next_turn", inserted.ProviderMessageId, null, inserted.UserRow),
                        SendResult.AuthRequired auth => MessageReceipt("queued", null, "auth_required", auth.UserRow),
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
                "send" => Delivery(scope, effect, allowDone: true) is { } delivery ? Positive(scope, delivery) ?? Receipt(EffectDisposition.Unknown, "delivery_unknown") : Receipt(EffectDisposition.Unknown, "delivery_owner_changed"),
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

    private static (string Chat, NeedAnswer Intent)? Answer(IReadScope scope, Effect effect)
    {
        var intent = effect.Payload.Deserialize(global::Assembly.Engine.EngineJson.Default.NeedAnswer);
        if (intent is null) return null;
        var chat = scope.QueryFirst("""
            SELECT chat_row_id FROM engine_needs_me WHERE id=$id AND session_id=$session AND provider_request_id=$request
            """, r => r.IsNull(0) ? null : r.Text(0), new SqlArg("$id", intent.NeedId), new("$session", effect.SessionId), new("$request", intent.ProviderRequestId));
        return chat is null ? null : (chat, intent);
    }

    private static EffectReceipt ReconcileAnswer(IReadScope scope, Effect effect)
    {
        var answer = Answer(scope, effect);
        if (answer is null) return Receipt(EffectDisposition.Unknown);
        var settled = scope.QueryFirst("""
            SELECT payload FROM rows WHERE session_id=$session AND item_id=$item AND kind='ask' ORDER BY seq DESC LIMIT 1
            """, r => JsonDocument.Parse(r.Text(0)).RootElement.Clone(), new SqlArg("$session", effect.SessionId), new("$item", "ask:" + answer.Value.Chat));
        if (settled.ValueKind == JsonValueKind.Object && settled.TryGetProperty("state", out var state) && state.GetString() == "answered" &&
            settled.TryGetProperty("answer", out var actual) && JsonElement.DeepEquals(actual, answer.Value.Intent.Answer))
            return new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new AnswerEffectData(answer.Value.Intent.ProviderRequestId), global::Assembly.Engine.EngineJson.Default.AnswerEffectData), null);
        return Receipt(EffectDisposition.Unknown);
    }
}
