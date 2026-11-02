using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Providers;

namespace Assembly.Engine.Messages;

internal static partial class AuthTransitions
{
    internal static void ValidateCompletionProjection(IWriteScope scope, CommandContext context, Effect effect,
        EffectReceipt receipt, MessageDelivery? delivery, MessageOutcome? outcome)
    {
        if (outcome is null) return;
        if (effect.Kind != "send" || receipt.Disposition != EffectDisposition.Confirmed || delivery is null)
            throw new CallFailed("bad_receipt");
        var data = receipt.Data.Deserialize(EngineJson.Default.MessageEffectData);
        if (data is not { State: "next_turn", ProviderTurnId: { } provider, UserRow: { } original } ||
            string.IsNullOrWhiteSpace(provider) || data.Reason is not null ||
            outcome != new MessageOutcome("failed", provider, "inserted_before_sign_in")) throw new CallFailed("bad_receipt");
        var users = scope.Query("""
            SELECT seq,run_id,payload FROM rows INDEXED BY rows_user_client WHERE session_id=$session
              AND json_extract(payload,'$.clientId')=$client AND kind='user' AND item_id IS NULL ORDER BY seq DESC LIMIT 2
            """, r => new { Seq = r.Long(0), Run = r.IsNull(1) ? null : r.Text(1), User = JsonSerializer.Deserialize(r.Text(2), EngineJson.Default.UserPayload) },
            new SqlArg("$session", delivery.Intent.ToSessionId), new("$client", delivery.Intent.ClientId));
        if (users.Count != 1) throw new CallFailed("bad_receipt");
        var row = users[0]; var user = row.User;
        if (row.Seq != original.Seq || row.Run != original.RunId || original.Epoch != delivery.Epoch || user is null ||
            user.Text != delivery.Intent.Text || user.From != delivery.FromSessionId || user.SenderLabel != delivery.Label ||
            user.Acknowledgment != new UserAcknowledgment("prompt_inserted", provider) || user.RetryExecution is not null)
            throw new CallFailed("bad_receipt");
        var terminals = scope.Query("""
            SELECT seq,payload FROM rows INDEXED BY rows_auth_terminal WHERE session_id=$session AND run_id=$run
              AND json_extract(payload,'$.authFailure.delivery.effectId')=$effect AND kind='turn' AND json_type(payload,'$.authFailure')='object'
            ORDER BY seq LIMIT 2
            """, r => new { Seq = r.Long(0), Turn = JsonSerializer.Deserialize(r.Text(1), EngineJson.Default.TurnPayload) },
            new SqlArg("$session", delivery.Intent.ToSessionId), new("$run", original.RunId), new("$effect", effect.Id));
        if (terminals.Count != 1 || terminals[0].Turn is not { State: "failed", AuthFailure: { } proof } ||
            proof.Version != 1 || proof.Epoch != delivery.Epoch || proof.ChatRunId != original.RunId ||
            proof.ProviderMessageId is not null && proof.ProviderMessageId != provider || proof.ClientId != delivery.Intent.ClientId ||
            proof.Delivery is not { } bound || bound.MessageId != delivery.Intent.MessageId || bound.EffectId != effect.Id ||
            bound.ClientId != delivery.Intent.ClientId || bound.UserRowSeq != original.Seq || bound.UserRunId != original.RunId ||
            bound.ClaimAttempt <= 0 || bound.ClaimAttempt > effect.Attempt ||
            !EngineCommands.IsAcceptedAuthTerminal(scope, delivery.Intent.ToSessionId, terminals[0].Seq, proof)) throw new CallFailed("bad_receipt");
        var notice = Notice(scope, context, delivery.Intent.ToSessionId, proof);
        if (notice.Count > 0) EngineJournal.Commit(scope, context, context.ExpectedVersion, context.Epoch, notice, []);
    }

    private static IReadOnlyList<EventDraft> Notice(IWriteScope scope, CommandContext context, string sessionId, AuthTerminalProof proof)
    {
        var original = proof.Delivery ?? throw new CallFailed("bad_receipt");
        var user = scope.QueryFirst("SELECT payload FROM rows WHERE session_id=$session AND seq=$seq AND run_id=$run AND kind='user' AND item_id IS NULL",
            r => JsonSerializer.Deserialize(r.Text(0), EngineJson.Default.UserPayload), new SqlArg("$session", sessionId), new("$seq", original.UserRowSeq), new("$run", original.UserRunId));
        if (user?.AuthBlocker is not { } blocker || blocker.EffectId != original.EffectId || blocker.OriginalClaimAttempt != original.ClaimAttempt ||
            blocker.ObservationId != proof.ObservationId || blocker.BlockCommandId != proof.BlockCommandId ||
            AuthNeed.Open(scope, context.Machine, blocker.NeedId) is not { } need || need.SessionId != sessionId ||
            need.Epoch != proof.Epoch || need.Version < blocker.NeedVersionAtBlock) throw new CallFailed("bad_receipt");
        return scope.Query("""
            UPDATE engine_needs_me SET text=CAST($text AS TEXT),version=version+1,updated_ms=$now
            WHERE id=$id AND state='open' AND CAST(text AS BLOB)=$base RETURNING version,run_id,step_id
            """, r => new EventDraft("needs_me.changed", "needs_me", need.Id, r.Long(0), false,
                new(sessionId, r.IsNull(1) ? null : r.Text(1), r.IsNull(2) ? null : r.Text(2), null, "open", proof.Epoch)),
            new("$text", Encoding.UTF8.GetBytes(AuthNeed.InsertionCopy(need.Target.Provider, context.Machine))), new("$now", context.NowMs),
            new("$id", need.Id), new("$base", Encoding.UTF8.GetBytes(AuthNeed.Copy(need.Target.Provider, context.Machine))));
    }
}
