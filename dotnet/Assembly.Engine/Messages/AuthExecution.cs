using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Providers;

namespace Assembly.Engine.Messages;

/// <summary>The checked original execution at the auth producer boundary; no later reservation supplies its authority.</summary>
internal sealed record AuthExecution(string SessionId, ConfiguredProviderTarget Target, UserPayload User,
    Effect Effect, MessageDelivery Delivery)
{
    internal static bool ClosedProof(AuthTerminalProof p) => p.ObservationId != Guid.Empty && p.BlockCommandId != Guid.Empty &&
        p.ObservationId != p.BlockCommandId && !string.IsNullOrWhiteSpace(p.ChatRunId) && p.Epoch >= 0 &&
        (p.Version == 1 && !string.IsNullOrWhiteSpace(p.AcpSessionId) && p.OpenFailure is null ||
         p.Version == 2 && p.AcpSessionId is null && p.ProviderMessageId is null && p.OpenFailure is { } stage && Enum.IsDefined(stage));

    internal static AuthExecution? Current(IReadScope scope, CommandContext context, string sessionId, AuthTerminalProof proof,
        Func<string, string, bool>? configured)
    {
        if (!ClosedProof(proof) || proof.Delivery is not { } original || original.UserRowSeq <= 0 ||
            original.ClaimAttempt <= 0 || string.IsNullOrWhiteSpace(original.UserRunId) ||
            string.IsNullOrWhiteSpace(original.EffectId) || string.IsNullOrWhiteSpace(original.MessageId) ||
            proof.ClientId != original.ClientId || string.IsNullOrWhiteSpace(proof.ClientId) || context.Epoch != proof.Epoch) return null;
        var owner = scope.QueryFirst("""
            SELECT provider,account_context,provider_session_id FROM engine_session
            WHERE id=$id AND machine=$machine AND holder='local' AND reserved_epoch IS NULL AND epoch=$epoch
              AND state NOT IN ('foreign','handing_off','ended')
            """, r => new { Target = new ConfiguredProviderTarget(r.Text(0), r.Text(1)), Acp = r.IsNull(2) ? null : r.Text(2) },
            new SqlArg("$id", sessionId), new("$machine", context.Machine), new("$epoch", proof.Epoch));
        if (owner is null || configured?.Invoke(owner.Target.Provider, owner.Target.AccountContext) != true ||
            proof.Version == 1 && owner.Acp != proof.AcpSessionId) return null;
        var provider = ProviderState.Read(scope, owner.Target.Id);
        if (provider is not null && (provider.Machine != context.Machine || provider.Target != owner.Target)) return null;
        var claimed = scope.QueryFirst("""
            SELECT id,kind,session_id,run_id,payload_json,attempt,order_key FROM engine_outbox
            WHERE id=$id AND machine=$machine AND kind='send' AND session_id=$session AND state='inflight' AND attempt=$claim
            """, r => new Effect(r.Text(0), r.Text(1), r.Text(2), r.IsNull(3) ? null : r.Text(3),
                JsonSerializer.Deserialize(r.Text(4), EngineJson.Default.JsonElement), checked((int)r.Long(5)), r.Text(6)),
            new SqlArg("$id", original.EffectId), new("$machine", context.Machine), new("$session", sessionId), new("$claim", original.ClaimAttempt));
        if (claimed is null || claimed.OrderKey != sessionId) return null;
        var delivery = MessageDelivery.Read(scope, claimed);
        if (delivery is null || delivery.Epoch != proof.Epoch || delivery.Intent.MessageId != original.MessageId ||
            delivery.Intent.ClientId != original.ClientId || delivery.Intent.How != "prompt") return null;
        var user = scope.QueryFirst("""
            SELECT payload FROM rows WHERE session_id=$session AND seq=$seq AND run_id=$run AND kind='user' AND item_id IS NULL
            """, r => JsonSerializer.Deserialize(r.Text(0), EngineJson.Default.UserPayload),
            new SqlArg("$session", sessionId), new("$seq", original.UserRowSeq), new("$run", original.UserRunId));
        if (user is null || user.ClientId != original.ClientId || user.Text != delivery.Intent.Text ||
            user.From != delivery.FromSessionId || user.SenderLabel != delivery.Label || user.Acknowledgment is not null ||
            original.UserRunId != proof.ChatRunId || user.RetryExecution is not null) return null;
        var started = scope.QueryFirst("""
            SELECT COUNT(*) FROM rows WHERE session_id=$session AND run_id=$run AND kind='turn'
              AND json_extract(payload,'$.state')='started' AND seq>$user
            """, r => r.Long(0), new SqlArg("$session", sessionId), new("$run", proof.ChatRunId), new("$user", original.UserRowSeq));
        return started == 1 ? new(sessionId, owner.Target, user, claimed, delivery) : null;
    }
}
