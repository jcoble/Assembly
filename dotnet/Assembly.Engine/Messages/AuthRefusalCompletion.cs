using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Providers;

namespace Assembly.Engine.Messages;

internal static partial class AuthTransitions
{
    private static MessageEffectData? RefusalData(Effect effect, EffectReceipt receipt) => effect.Kind == "send" && receipt.Disposition == EffectDisposition.Confirmed &&
        receipt.Data.Deserialize(EngineJson.Default.MessageEffectData) is { State: "queued", Reason: "auth_required", ProviderTurnId: null, UserRow: not null } data ? data : null;

    private static (UserPayload User, ConfiguredProviderTarget Target) RefusalUser(IReadScope scope, CommandContext context,
        Effect effect, MessageDelivery delivery, MessageEffectData data)
    {
        var original = data.UserRow!;
        var target = scope.QueryFirst("""
            SELECT provider,account_context FROM engine_session WHERE id=$session AND machine=$machine AND holder='local'
              AND reserved_epoch IS NULL AND epoch=$epoch AND state NOT IN ('foreign','handing_off','ended')
            """, r => new ConfiguredProviderTarget(r.Text(0), r.Text(1)), new SqlArg("$session", delivery.Intent.ToSessionId), new("$machine", context.Machine), new("$epoch", delivery.Epoch));
        var user = scope.QueryFirst("""
            SELECT payload FROM rows WHERE session_id=$session AND seq=$seq AND run_id=$run AND kind='user' AND item_id IS NULL
            """, r => JsonSerializer.Deserialize(r.Text(0), EngineJson.Default.UserPayload), new SqlArg("$session", delivery.Intent.ToSessionId), new("$seq", original.Seq), new("$run", original.RunId));
        if (target is null || target.Provider.Length == 0 || !AccountContext.IsValid(target.AccountContext) || original.Epoch != delivery.Epoch ||
            context.Epoch != delivery.Epoch || effect.SessionId != delivery.Intent.ToSessionId || effect.OrderKey != effect.SessionId || delivery.Intent.How != "prompt" ||
            user is null || user.ClientId != delivery.Intent.ClientId || user.Text != delivery.Intent.Text || user.From != delivery.FromSessionId || user.SenderLabel != delivery.Label ||
            user.Acknowledgment is not null || user.RetryExecution is not null || data.RetryExecution is not null ||
            scope.QueryFirst("""
                SELECT COUNT(*) FROM rows WHERE session_id=$session AND run_id=$run AND kind='turn' AND seq>$seq AND json_extract(payload,'$.state')='started'
                """, r => r.Long(0), new SqlArg("$session", delivery.Intent.ToSessionId), new("$run", original.RunId), new("$seq", original.Seq)) != 1) throw new CallFailed("bad_receipt");
        var provider = ProviderState.Read(scope, target.Id);
        if (provider is not null && (provider.Machine != context.Machine || provider.Target != target)) throw new CallFailed("bad_receipt");
        return (user, target);
    }

    internal static long? PrepareRefusalCompletion(IWriteScope scope, CommandContext context, Effect effect, EffectReceipt receipt,
        MessageDelivery? delivery, MessageExpiryPolicy? expiryPolicy)
    {
        if (RefusalData(effect, receipt) is not { } data) return null;
        if (delivery is null || data.AuthRefusal is not null) throw new CallFailed("bad_receipt");
        var captured = RefusalUser(scope, context, effect, delivery, data);
        var message = scope.QueryFirst("SELECT attempt,expires_ms FROM engine_message WHERE id=$id", r => (Attempt: r.Long(0), Expires: r.IsNull(1) ? (long?)null : r.Long(1)), new SqlArg("$id", delivery.Intent.MessageId));
        if (captured.User.AuthBlocker is null)
        {
            // Ordinary expiry grants neither a new need nor historical auth authority.
            if (message.Expires <= context.NowMs) return null;
            var result = BindRefusalNeed(scope, context, new(delivery.Intent.ToSessionId, captured.Target, captured.User, effect, delivery), data.UserRow!, context.CommandId, null, expiryPolicy ?? new());
            if (!result.Applied) throw new CallFailed("bad_receipt");
        }
        _ = RefusalOutcome(scope, context, effect, receipt, delivery);
        return message.Attempt;
    }

    internal static MessageOutcome? RefusalOutcome(IReadScope scope, CommandContext context, Effect effect, EffectReceipt receipt, MessageDelivery delivery)
    {
        if (RefusalData(effect, receipt) is not { } data) return null;
        var captured = RefusalUser(scope, context, effect, delivery, data);
        var blocker = captured.User.AuthBlocker;
        if (blocker is null)
        {
            if (scope.QueryFirst("SELECT expires_ms FROM engine_message WHERE id=$id", r => r.IsNull(0) ? (long?)null : r.Long(0), new SqlArg("$id", delivery.Intent.MessageId)) <= context.NowMs) return null;
            throw new CallFailed("bad_receipt");
        }
        if (blocker.EffectId != effect.Id || blocker.OriginalClaimAttempt != effect.Attempt || blocker.ObservationId == Guid.Empty ||
            blocker.BlockCommandId == Guid.Empty || AuthNeed.Refusal(scope, context.Machine, delivery.Intent.ToSessionId, captured.Target, delivery.Epoch, blocker) is not { } bound)
            throw new CallFailed("bad_receipt");
        return bound.State switch { "answered" => new("queued", null, "not_delivered"), "dismissed" => new("failed", null, "sign_in_dismissed"), _ => null };
    }

    internal static EffectReceipt RetainRefusalReceipt(IWriteScope scope, CommandContext context, Effect effect, MessageDelivery delivery,
        EffectReceipt receipt, long originalMessageAttempt)
    {
        var data = RefusalData(effect, receipt) ?? throw new CallFailed("bad_receipt");
        var captured = RefusalUser(scope, context, effect, delivery, data);
        _ = RefusalOutcome(scope, context, effect, receipt, delivery);
        var blocker = captured.User.AuthBlocker ?? throw new CallFailed("bad_receipt");
        if (originalMessageAttempt <= 0 || effect.Id != "send/" + delivery.Intent.MessageId + "/" + originalMessageAttempt &&
            !(originalMessageAttempt == 1 && effect.Id == "send/" + delivery.Intent.MessageId && delivery.Intent.ClientId == effect.Id &&
              scope.QueryFirst("SELECT client_id FROM engine_message WHERE id=$id", r => r.IsNull(0) ? null : r.Text(0), new SqlArg("$id", delivery.Intent.MessageId)) == effect.Id)) throw new CallFailed("bad_receipt");
        return receipt with { Data = JsonSerializer.SerializeToElement(data with { AuthRefusal = new(originalMessageAttempt, blocker) }, EngineJson.Default.MessageEffectData) };
    }
}
