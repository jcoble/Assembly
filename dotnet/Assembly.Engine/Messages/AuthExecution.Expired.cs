using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Providers;

namespace Assembly.Engine.Messages;

internal sealed partial record AuthExecution
{
    // Ordinary terminal routing only: this creates no accepted auth-terminal or retry authority.
    internal static bool ExpiredUnbound(IReadScope scope, CommandContext context, string session, AuthTerminalProof proof,
        Func<string, string, bool>? configured)
    {
        if (!ClosedProof(proof) || proof.ProviderMessageId is not null || proof.Delivery is not { } original ||
            original.ClaimAttempt <= 0 || original.UserRowSeq <= 0 || proof.ClientId != original.ClientId ||
            original.UserRunId != proof.ChatRunId || proof.Epoch != context.Epoch) return false;
        try
        {
            var owner = scope.QueryFirst("""
                SELECT provider,account_context,provider_session_id FROM engine_session WHERE id=$id AND machine=$machine
                  AND holder='local' AND reserved_epoch IS NULL AND epoch=$epoch AND state NOT IN ('foreign','handing_off','ended')
                """, r => new { Target = new ConfiguredProviderTarget(r.Text(0), r.Text(1)), Acp = r.IsNull(2) ? null : r.Text(2) },
                new SqlArg("$id", session), new("$machine", context.Machine), new("$epoch", proof.Epoch));
            if (owner is null || owner.Target.Provider.Length == 0 || !AccountContext.IsValid(owner.Target.AccountContext) ||
                configured?.Invoke(owner.Target.Provider, owner.Target.AccountContext) != true || proof.Version == 1 && owner.Acp != proof.AcpSessionId) return false;
            var provider = ProviderState.Read(scope, owner.Target.Id);
            if (provider is not null && (provider.Machine != context.Machine || provider.Target != owner.Target)) return false;
            var done = scope.QueryFirst("""
                SELECT id,kind,session_id,run_id,payload_json,attempt,order_key,receipt_json,updated_ms FROM engine_outbox
                WHERE id=$id AND machine=$machine AND kind='send' AND session_id=$session AND state='done' AND attempt=$claim
                """, r => new
            {
                Effect = new Effect(r.Text(0), r.Text(1), r.Text(2), r.IsNull(3) ? null : r.Text(3), JsonSerializer.Deserialize(r.Text(4), EngineJson.Default.JsonElement), checked((int)r.Long(5)), r.Text(6)),
                Receipt = r.IsNull(7) ? null : JsonSerializer.Deserialize(r.Text(7), EngineJson.Default.EffectReceipt),
                At = r.Long(8)
            },
                new SqlArg("$id", original.EffectId), new("$machine", context.Machine), new("$session", session), new("$claim", original.ClaimAttempt));
            if (done?.Receipt is not { Disposition: EffectDisposition.Confirmed } receipt || done.Effect.OrderKey != session ||
                receipt.Data.Deserialize(EngineJson.Default.MessageEffectData) is not { State: "queued", Reason: "auth_required", ProviderTurnId: null, UserRow: { } reference, AuthRefusal: null, RetryExecution: null } ||
                reference.Seq != original.UserRowSeq || reference.RunId != original.UserRunId || reference.Epoch != proof.Epoch) return false;
            if (scope.QueryFirst($"""
                SELECT COUNT(*) FROM engine_message m WHERE m.id=$id AND m.machine=$machine AND m.to_session_id=$session
                  AND m.state='failed' AND m.reason='expired' AND m.in_flight=0 AND m.provider_turn_id IS NULL
                  AND m.expires_ms IS NOT NULL AND m.expires_ms<=$completed AND {MessageExpiry.ReservationFree}
                """, r => r.Long(0), new SqlArg("$id", original.MessageId), new("$machine", context.Machine), new("$session", session), new("$completed", done.At)) != 1) return false;
            // Here only, allow the finalized original M; canonical attempt/body/refs remain fully checked.
            var delivery = MessageDelivery.Read(scope, done.Effect, allowDone: true);
            if (delivery is null || delivery.Epoch != proof.Epoch || delivery.Intent.MessageId != original.MessageId ||
                delivery.Intent.ClientId != original.ClientId || delivery.Intent.How != "prompt") return false;
            var user = scope.QueryFirst("""
                SELECT payload FROM rows WHERE session_id=$session AND seq=$seq AND run_id=$run AND kind='user' AND item_id IS NULL
                """, r => JsonSerializer.Deserialize(r.Text(0), EngineJson.Default.UserPayload), new SqlArg("$session", session), new("$seq", original.UserRowSeq), new("$run", original.UserRunId));
            if (user is null || user.ClientId != original.ClientId || user.Text != delivery.Intent.Text || user.From != delivery.FromSessionId ||
                user.SenderLabel != delivery.Label || user.Acknowledgment is not null || user.AuthBlocker is not null || user.RetryExecution is not null) return false;
            return scope.QueryFirst("""
                SELECT COUNT(*) FROM rows WHERE session_id=$session AND run_id=$run AND kind='turn' AND seq>$user AND json_extract(payload,'$.state')='started'
                """, r => r.Long(0), new SqlArg("$session", session), new("$run", proof.ChatRunId), new("$user", original.UserRowSeq)) == 1;
        }
        catch (JsonException) { return false; }
        catch (System.Text.DecoderFallbackException) { return false; }
    }
}
