using Assembly.Application.Common.Engine;
using Assembly.Infrastructure.Engine.Auth;
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
    private EffectReceipt? Positive(IReadScope scope, Effect effect, MessageDelivery delivery)
    {
        var row = rows.UserDeliveryOfClient(scope, delivery.Intent.ToSessionId, delivery.Intent.ClientId);
        if (row is null || row.Text != delivery.Intent.Text || row.From != delivery.FromSessionId || row.SenderLabel != delivery.Label || !RowStore.Positive(row.Acknowledgment)) return null;
        if (row.RetryExecution is { } retry && !RetryMatches(scope, effect, delivery, row, retry)) throw new CallFailed("bad_receipt");
        return row.Acknowledgment!.Kind == "prompt_inserted"
            ? MessageReceipt("next_turn", row.Acknowledgment.ProviderMessageId, null,
                row.RetryExecution is null ? null : new(row.Seq, row.RunId!, delivery.Epoch), row.RetryExecution)
            : MessageReceipt("delivered_to_turn", null, null);
    }

    private EffectReceipt ReconcileAuthDelivery(IReadScope scope, Effect effect, MessageDelivery delivery)
    {
        if (Positive(scope, effect, delivery) is { } positive) return positive;
        var row = rows.UserDeliveryOfClient(scope, delivery.Intent.ToSessionId, delivery.Intent.ClientId);
        if (row is not null && AuthDeliveryProof.Read(scope, effect, delivery, row) is { Version: 1, ProviderMessageId: { } provider } proof &&
            !string.IsNullOrWhiteSpace(provider))
            return MessageReceipt("next_turn", provider, null, new(row.Seq, proof.Delivery!.UserRunId, proof.Epoch), row.RetryExecution);
        return Receipt(EffectDisposition.Unknown, "delivery_unknown");
    }

    private static OwningSendAdmission Admission(IReadScope scope, Effect effect, MessageDelivery delivery)
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
            delivery.Label, delivery.Epoch, reference, intent.AttachmentIds ?? [], priorId, delivery.FromSessionId,
            scope.QueryFirst("SELECT expires_ms FROM engine_message WHERE id=$id", r => r.IsNull(0) ? (long?)null : r.Long(0), new SqlArg("$id", intent.MessageId)));
    }

    public MessageOutcome? ProjectCompletion(IWriteScope scope, CommandContext context, Effect effect, EffectReceipt receipt, MessageDelivery delivery)
    {
        if (effect.Kind != "send") return null;
        if (!Owns(scope, effect)) throw new CallFailed("delivery_ack_conflict");
        if (context.Epoch != delivery.Epoch) throw new CallFailed("delivery_ack_conflict");
        var data = receipt.Data.Deserialize(global::Assembly.Engine.EngineJson.Default.MessageEffectData)!;
        if (data.State is not ("next_turn" or "delivered_to_turn") && data.UserRow is null) return null;
        var row = rows.UserDeliveryOfClient(scope, delivery.Intent.ToSessionId, delivery.Intent.ClientId)
            ?? throw new CallFailed("delivery_ack_conflict");
        if (row.Text != delivery.Intent.Text || row.From != delivery.FromSessionId || row.SenderLabel != delivery.Label) throw new CallFailed("delivery_ack_conflict");
        if (data.RetryExecution != row.RetryExecution || data.RetryExecution is { } retry && !RetryMatches(scope, effect, delivery, row, retry))
            throw new CallFailed("delivery_ack_conflict");
        if (data.UserRow is { } reference)
        {
            if (reference.Seq != row.Seq || reference.RunId != row.RunId || reference.Epoch != delivery.Epoch)
                throw new CallFailed("delivery_ack_conflict");
        }
        else if (!RowStore.Positive(row.Acknowledgment)) throw new CallFailed("delivery_ack_conflict");
        if (data.State == "queued" && data.Reason == "auth_required") return global::Assembly.Engine.Messages.AuthTransitions.RefusalOutcome(scope, context, effect, receipt, delivery);
        var acknowledgment = data.State switch
        {
            "next_turn" when !string.IsNullOrWhiteSpace(data.ProviderTurnId) => new UserAcknowledgment("prompt_inserted", data.ProviderTurnId),
            "delivered_to_turn" when data.ProviderTurnId is null => new UserAcknowledgment("steer_injected", null),
            _ => throw new CallFailed("delivery_ack_conflict")
        };
        rows.AcknowledgeUser(scope, delivery.Intent.ToSessionId, delivery.Intent.ClientId, acknowledgment);
        if (data.State != "next_turn" || AuthDeliveryProof.Read(scope, effect, delivery, row) is not { } proof) return null;
        if (proof.Version != 1 || proof.ProviderMessageId is not null && proof.ProviderMessageId != data.ProviderTurnId)
            throw new CallFailed("bad_receipt");
        return new("failed", data.ProviderTurnId, "inserted_before_sign_in");
    }

    private static bool RetryMatches(IReadScope scope, Effect effect, MessageDelivery delivery, UserDeliveryRow row, AuthRetryExecution retry) =>
        retry.EffectId == effect.Id && retry.ClaimAttempt > 0 && retry.ClaimAttempt <= effect.Attempt && retry.MessageId == delivery.Intent.MessageId &&
        retry.ClientId == delivery.Intent.ClientId && retry.Epoch == delivery.Epoch && retry.UserRowSeq == row.Seq && retry.UserRunId == row.RunId &&
        retry.ChatRunId != row.RunId && global::Assembly.Engine.EngineCommands.IsAcceptedAuthRetryStarted(scope, delivery.Intent.ToSessionId, retry);
}
