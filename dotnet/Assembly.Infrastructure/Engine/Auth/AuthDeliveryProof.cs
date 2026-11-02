using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;

namespace Assembly.Infrastructure.Engine.Auth;

/// <summary>Closed retained insertion read for this exact validated delivery, never a latest-message or event-history lookup.</summary>
internal static class AuthDeliveryProof
{
    internal static AuthTerminalProof? Read(IReadScope scope, Effect effect, MessageDelivery delivery, UserDeliveryRow row)
    {
        if (row.RunId is null || row.RetryExecution is not null) throw new CallFailed("bad_receipt");
        var terminals = scope.Query("""
            SELECT seq,payload FROM rows INDEXED BY rows_auth_terminal
            WHERE session_id=$session AND run_id=$run AND json_extract(payload,'$.authFailure.delivery.effectId')=$effect
              AND kind='turn' AND json_type(payload,'$.authFailure')='object' ORDER BY seq LIMIT 2
            """, r => new { Seq = r.Long(0), Turn = JsonSerializer.Deserialize(r.Text(1), EngineJson.Default.TurnPayload) },
            new SqlArg("$session", delivery.Intent.ToSessionId), new("$run", row.RunId), new("$effect", effect.Id));
        if (terminals.Count == 0) return null;
        if (terminals.Count != 1 || terminals[0].Turn is not { State: "failed", AuthFailure: { } proof } ||
            row.Text != delivery.Intent.Text || row.From != delivery.FromSessionId || row.SenderLabel != delivery.Label ||
            row.ClientId != delivery.Intent.ClientId || proof.ClientId != row.ClientId || proof.ChatRunId != row.RunId ||
            proof.Epoch != delivery.Epoch || proof.Delivery is not { } original || original.MessageId != delivery.Intent.MessageId ||
            original.EffectId != effect.Id || original.ClientId != row.ClientId || original.UserRowSeq != row.Seq || original.UserRunId != row.RunId ||
            original.ClaimAttempt <= 0 || original.ClaimAttempt > effect.Attempt || row.AuthBlocker is not { } blocker ||
            blocker.EffectId != effect.Id || blocker.OriginalClaimAttempt != original.ClaimAttempt || blocker.ObservationId != proof.ObservationId ||
            blocker.BlockCommandId != proof.BlockCommandId ||
            !EngineCommands.IsAcceptedAuthTerminal(scope, delivery.Intent.ToSessionId, terminals[0].Seq, proof)) throw new CallFailed("bad_receipt");
        return proof;
    }
}
