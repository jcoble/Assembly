using Assembly.Application.Common.Engine;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;

namespace Assembly.Infrastructure.Engine;

public sealed partial class AdapterEffects
{
    private sealed record DeliveryIntent(DeliverMessage Intent, string? Label, long Epoch, string? FromSessionId);

    private static DeliveryIntent? Delivery(IReadScope scope, Effect effect, bool allowDone = false)
    {
        var intent = effect.Payload.Deserialize(global::Assembly.Engine.EngineJson.Default.DeliverMessage);
        if (intent is null || intent.ToSessionId != effect.SessionId || intent.How is not ("prompt" or "steer")) return null;
        return scope.QueryFirst("""
            SELECT CAST(m.text AS BLOB),CAST(m.sender_label AS BLOB),COALESCE(m.client_id,m.id),m.framed,m.held_epoch,m.attachment_ids_json,
                s.epoch,m.state,m.in_flight,s.state,m.attempt,m.client_id,m.from_session_id,m.sender_kind
            FROM engine_message m JOIN engine_session s ON s.id=m.to_session_id
            JOIN engine_meta meta ON meta.singleton=1 AND s.machine=meta.machine_id AND m.machine=meta.machine_id
            WHERE m.id=$message AND m.to_session_id=$session AND s.holder='local'
            """, r =>
        {
            var framed = r.Long(3) == 1;
            var body = new UTF8Encoding(false, true).GetString(r.Blob(0));
            var label = new UTF8Encoding(false, true).GetString(r.Blob(1));
            var canonical = effect.Id == $"send/{intent.MessageId}/{r.Long(10)}";
            var legacy = r.Long(10) == 1 && effect.Id == "send/" + intent.MessageId && !r.IsNull(11) && r.Text(11) == effect.Id && intent.ClientId == effect.Id;
            if (!canonical && !legacy) return null;
            var expected = framed && !legacy ? MessageText.Prompt(label, body) : body;
            var refs = JsonSerializer.Deserialize(r.Text(5), global::Assembly.Engine.EngineJson.Default.IReadOnlyListString)!;
            if (intent.Text != expected || intent.SenderLabel != label || intent.ClientId != r.Text(2) ||
                (intent.Framed != false) != framed || (!framed && (intent.HeldEpoch != r.Long(6) ||
                    r.IsNull(4) || r.Long(4) != intent.HeldEpoch || !(intent.AttachmentIds ?? []).SequenceEqual(refs))) ||
                !allowDone && (r.Text(7) != "queued" || r.Long(8) != 1 || r.Text(9) == "ended")) return null;
            var from = framed && r.Text(13) == "session" ? r.IsNull(12) ? null : r.Text(12) : null;
            if (framed && r.Text(13) == "session" && from is null) return null;
            return new DeliveryIntent(intent, framed ? intent.SenderLabel : null, r.Long(6), from);
        }, new SqlArg("$message", intent.MessageId), new("$session", effect.SessionId));
    }

    private EffectReceipt? Positive(IReadScope scope, DeliveryIntent delivery)
    {
        var row = rows.UserDeliveryOfClient(scope, delivery.Intent.ToSessionId, delivery.Intent.ClientId);
        if (row is null || row.Text != delivery.Intent.Text || row.From != delivery.FromSessionId || row.SenderLabel != delivery.Label || !RowStore.Positive(row.Acknowledgment)) return null;
        return row.Acknowledgment!.Kind == "prompt_inserted"
            ? MessageReceipt("next_turn", row.Acknowledgment.ProviderMessageId, null)
            : MessageReceipt("delivered_to_turn", null, null);
    }

    private static OwningSendAdmission Admission(IReadScope scope, Effect effect, DeliveryIntent delivery)
    {
        var intent = delivery.Intent;
        var previous = scope.QueryFirst("""
            SELECT id,payload_json,receipt_json,state FROM engine_outbox
            WHERE kind='send' AND session_id=$session AND id<>$id AND rowid<(SELECT rowid FROM engine_outbox WHERE id=$id)
              AND json_extract(payload_json,'$.messageId')=$message
            ORDER BY event_seq DESC,rowid DESC LIMIT 1
            """, r => (Id: r.Text(0), Payload: r.Text(1), Receipt: r.IsNull(2) ? null : r.Text(2), State: r.Text(3)),
            new SqlArg("$session", effect.SessionId), new("$id", effect.Id), new("$message", intent.MessageId));
        UserRowReference? reference = null; string? priorId = null;
        if (previous.State == "done" && previous.Receipt is not null)
        {
            var prior = JsonSerializer.Deserialize(previous.Payload, global::Assembly.Engine.EngineJson.Default.DeliverMessage);
            var receipt = JsonSerializer.Deserialize(previous.Receipt, global::Assembly.Engine.EngineJson.Default.EffectReceipt);
            var result = receipt?.Disposition == EffectDisposition.Confirmed ? receipt.Data.Deserialize(global::Assembly.Engine.EngineJson.Default.MessageEffectData) : null;
            if (prior is not null && result is { State: "queued", Reason: "auth_required", UserRow: { } original } &&
                original.Epoch == delivery.Epoch && prior.MessageId == intent.MessageId && prior.ToSessionId == intent.ToSessionId &&
                prior.ClientId == intent.ClientId && prior.Text == intent.Text && prior.SenderLabel == intent.SenderLabel &&
                prior.Framed == intent.Framed && prior.HeldEpoch == intent.HeldEpoch &&
                (prior.AttachmentIds ?? []).SequenceEqual(intent.AttachmentIds ?? []))
            { reference = original; priorId = previous.Id; }
        }
        return new(effect.Id, intent.MessageId, effect.Attempt, intent.ToSessionId, intent.ClientId, intent.Text,
            delivery.Label, delivery.Epoch, reference, intent.AttachmentIds ?? [], priorId, delivery.FromSessionId);
    }

    public void ProjectCompletion(IWriteScope scope, CommandContext context, Effect effect, EffectReceipt receipt)
    {
        if (effect.Kind != "send") return;
        if (!Owns(scope, effect)) throw new CallFailed("delivery_ack_conflict");
        var delivery = Delivery(scope, effect) ?? throw new CallFailed("delivery_ack_conflict");
        if (context.Epoch != delivery.Epoch) throw new CallFailed("delivery_ack_conflict");
        var data = receipt.Data.Deserialize(global::Assembly.Engine.EngineJson.Default.MessageEffectData)!;
        if (data.State is not ("next_turn" or "delivered_to_turn") && data.UserRow is null) return;
        var row = rows.UserDeliveryOfClient(scope, delivery.Intent.ToSessionId, delivery.Intent.ClientId)
            ?? throw new CallFailed("delivery_ack_conflict");
        if (row.Text != delivery.Intent.Text || row.From != delivery.FromSessionId || row.SenderLabel != delivery.Label) throw new CallFailed("delivery_ack_conflict");
        if (data.UserRow is { } reference)
        {
            if (reference.Seq != row.Seq || reference.RunId != row.RunId || reference.Epoch != delivery.Epoch)
                throw new CallFailed("delivery_ack_conflict");
        }
        else if (!RowStore.Positive(row.Acknowledgment)) throw new CallFailed("delivery_ack_conflict");
        if (data.State == "queued" && data.Reason == "auth_required") return;
        var acknowledgment = data.State switch
        {
            "next_turn" when !string.IsNullOrWhiteSpace(data.ProviderTurnId) => new UserAcknowledgment("prompt_inserted", data.ProviderTurnId),
            "delivered_to_turn" when data.ProviderTurnId is null => new UserAcknowledgment("steer_injected", null),
            _ => throw new CallFailed("delivery_ack_conflict")
        };
        rows.AcknowledgeUser(scope, delivery.Intent.ToSessionId, delivery.Intent.ClientId, acknowledgment);
    }
}
