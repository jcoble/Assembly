using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Providers;

namespace Assembly.Engine.Messages;

internal static class AuthRetryStart
{
    internal static CommandContext Context(string machine, long now, AuthRetryExecution execution) =>
        new(Guid.Parse(execution.ChatRunId), machine, new(ActorKind.System, "adapter"), "auth-retry/" + execution.ChatRunId, 0, execution.Epoch, now);

    internal static bool Started(IReadScope scope, string session, AuthRetryExecution execution) =>
        scope.QueryFirst("""
            SELECT COUNT(*) FROM rows WHERE session_id=$session AND run_id=$run AND kind='turn' AND item_id IS NULL
              AND seq>$user AND json_extract(payload,'$.state')='started'
            """, r => r.Long(0), new SqlArg("$session", session), new("$run", execution.ChatRunId), new("$user", execution.UserRowSeq)) == 1;

    internal static CommandResult Apply(IWriteScope scope, CommandContext context, RecordAuthRetryStarted command,
        Func<string, string, bool>? configured)
    {
        var e = command.Execution;
        CommandResult Refused() => new(false, false, "bad_args", 0, 0, context.Epoch);
        if (!Guid.TryParse(e.ChatRunId, out var id) || id == Guid.Empty || e.ChatRunId == e.UserRunId ||
            e.UserRowSeq <= 0 || e.ClaimAttempt <= 0 || e.EffectId == e.PriorEffectId ||
            context != Context(context.Machine, context.NowMs, e) || !Started(scope, command.SessionId, e)) return Refused();
        var target = scope.QueryFirst("""
            SELECT provider,account_context FROM engine_session WHERE id=$session AND machine=$machine AND holder='local'
              AND epoch=$epoch AND reserved_epoch IS NULL AND state NOT IN ('foreign','handing_off','ended')
            """, r => new ConfiguredProviderTarget(r.Text(0), r.Text(1)), new SqlArg("$session", command.SessionId),
            new("$machine", context.Machine), new("$epoch", e.Epoch));
        if (target is null || configured?.Invoke(target.Provider, target.AccountContext) != true) return Refused();
        var current = scope.QueryFirst("""
            SELECT kind,run_id,payload_json,order_key FROM engine_outbox
            WHERE id=$id AND machine=$machine AND session_id=$session AND state='inflight' AND attempt=$claim AND kind='send'
            """, r => new Effect(e.EffectId, r.Text(0), command.SessionId, r.IsNull(1) ? null : r.Text(1),
                JsonSerializer.Deserialize(r.Text(2), EngineJson.Default.JsonElement), e.ClaimAttempt, r.Text(3)),
            new SqlArg("$id", e.EffectId), new("$machine", context.Machine), new("$session", command.SessionId), new("$claim", e.ClaimAttempt));
        var delivery = current is null ? null : MessageDelivery.Read(scope, current);
        if (delivery is null || current!.OrderKey != command.SessionId || delivery.Epoch != e.Epoch ||
            delivery.Intent.MessageId != e.MessageId || delivery.Intent.ClientId != e.ClientId || delivery.Intent.How != "prompt") return Refused();
        if (scope.QueryFirst("SELECT expires_ms FROM engine_message WHERE id=$id", r => r.IsNull(0) ? (long?)null : r.Long(0),
                new SqlArg("$id", e.MessageId)) <= context.NowMs)
            return new(false, false, "expired", 0, 0, context.Epoch);
        var user = scope.QueryFirst("SELECT payload FROM rows WHERE session_id=$session AND seq=$seq AND run_id=$run AND kind='user' AND item_id IS NULL",
            r => JsonSerializer.Deserialize(r.Text(0), EngineJson.Default.UserPayload), new SqlArg("$session", command.SessionId), new("$seq", e.UserRowSeq), new("$run", e.UserRunId));
        if (user is null || user.ClientId != e.ClientId || user.Text != delivery.Intent.Text || user.From != delivery.FromSessionId ||
            user.SenderLabel != delivery.Label || user.Acknowledgment is not null || user.AuthBlocker is not { } blocker || blocker.EffectId != e.PriorEffectId ||
            user.RetryExecution is { } old && old.EffectId != e.PriorEffectId ||
            AuthNeed.Refusal(scope, context.Machine, command.SessionId, target, e.Epoch, blocker) is not { State: "answered" }) return Refused();
        var prior = scope.QueryFirst("""
            SELECT payload_json,receipt_json,attempt FROM engine_outbox WHERE id=$id AND machine=$machine AND session_id=$session
              AND kind='send' AND state='done' AND rowid<(SELECT rowid FROM engine_outbox WHERE id=$current)
            """, r => new { Payload = r.Text(0), Receipt = r.IsNull(1) ? null : r.Text(1), Attempt = r.Long(2) },
            new SqlArg("$id", e.PriorEffectId), new("$machine", context.Machine), new("$session", command.SessionId), new("$current", e.EffectId));
        if (prior?.Receipt is null || JsonSerializer.Deserialize(prior.Payload, EngineJson.Default.DeliverMessage) is not { } intent ||
            JsonSerializer.Deserialize(prior.Receipt, EngineJson.Default.EffectReceipt) is not { Disposition: EffectDisposition.Confirmed } receipt ||
            receipt.Data.Deserialize(EngineJson.Default.MessageEffectData) is not
            {
                State: "queued", Reason: "auth_required", ProviderTurnId: null,
                UserRow: { } original, AuthRefusal: { } capsule
            } data ||
            capsule.Blocker != blocker || prior.Attempt != blocker.OriginalClaimAttempt ||
            original.Seq != e.UserRowSeq || original.RunId != e.UserRunId || original.Epoch != e.Epoch ||
            intent.MessageId != e.MessageId || intent.ToSessionId != command.SessionId || intent.ClientId != e.ClientId ||
            intent.Text != delivery.Intent.Text || intent.SenderLabel != delivery.Intent.SenderLabel || intent.Framed != delivery.Intent.Framed ||
            intent.HeldEpoch != delivery.Intent.HeldEpoch || !(intent.AttachmentIds ?? []).SequenceEqual(delivery.Intent.AttachmentIds ?? []) ||
            data.RetryExecution != user.RetryExecution ||
            data.RetryExecution is { } previous && (previous.EffectId != e.PriorEffectId || previous.MessageId != e.MessageId ||
                previous.UserRowSeq != e.UserRowSeq || previous.UserRunId != e.UserRunId || previous.ClientId != e.ClientId || previous.Epoch != e.Epoch ||
                !EngineCommands.IsAcceptedAuthRetryStarted(scope, command.SessionId, previous)) ||
            !EngineCommands.HasPriorAuthTerminal(scope, command.SessionId, data.RetryExecution?.ChatRunId ?? e.UserRunId)) return Refused();
        RowStore.BindAuthRetryExecution(scope, command.SessionId, e.ClientId, e.UserRowSeq, e.UserRunId, e);
        return EngineJournal.Commit(scope, context, 0, e.Epoch, [], []);
    }
}
