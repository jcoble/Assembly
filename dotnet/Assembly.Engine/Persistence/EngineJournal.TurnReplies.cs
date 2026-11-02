using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Persistence;

public static partial class EngineJournal
{
    internal static CommandResult ApplyTurnReply(IWriteScope scope, string machine, PreparedTurnReply reply,
        long nowMs, MessageExpiryPolicy policy, bool recovery)
    {
        if ((reply.Origin == TurnReplyOrigin.Recovery) != recovery) throw new ArgumentException("Wrong turn reply origin.");
        var id = reply.MessageId(machine); var key = "turn-reply/v1/" + id["turn-reply/".Length..];
        var context = new CommandContext(Guid.Empty, machine, new(ActorKind.System,
            recovery ? "adapter:turn-recovery" : "adapter:turn-reply"), id, 0, reply.Epoch, nowMs);
        var admission = new TurnReplyAdmission(scope, context, key, reply, policy);
        return ExecuteCore(scope, context, key, admission.Canonical, admission.Transition, turnReplyAdmission: admission);
    }

    internal static bool HasPriorAuthReply(IReadScope scope, string machine, FinalizedChatReply source, string parent)
    {
        var id = PreparedTurnReply.MessageId(machine, source.SessionId, source.ChatRunId);
        var key = "turn-reply/v1/" + id["turn-reply/".Length..];
        var receipt = scope.QueryFirst("SELECT fingerprint,result_json FROM engine_command_receipt WHERE command_id=$id",
            r => new { Fingerprint = r.Text(0), Result = r.Text(1) }, new SqlArg("$id", key));
        if (receipt is null || !TurnReplyFingerprint.TryDecode(receipt.Fingerprint, machine, source.SessionId, id, out var carrier) ||
            carrier!.TerminalSeq != source.TerminalSeq || carrier.ChatRunIdSha256 != RelayHash.Sha(RelayHash.Utf8.GetBytes(source.ChatRunId)) ||
            System.Text.Json.JsonSerializer.Deserialize(receipt.Result, EngineJson.Default.CommandResult)?.Applied != true) return false;
        return scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_message WHERE id=$id AND machine=$machine AND from_session_id=$child AND to_session_id=$parent
              AND sender_kind='session' AND mode='prompt' AND framed=1 AND CAST(text AS BLOB)=$body
            """, r => r.Long(0), new SqlArg("$id", id), new("$machine", machine), new("$child", source.SessionId), new("$parent", parent),
            new("$body", RelayHash.Utf8.GetBytes(source.Text))) == 1;
    }

    private sealed class TurnReplyAdmission(IWriteScope scope, CommandContext context, string key, PreparedTurnReply reply, MessageExpiryPolicy policy)
    {
        internal string Canonical { get; } = reply.Canonical(context.Machine);
        internal void ValidateBindings(IWriteScope actualScope, CommandContext actualContext, string actualKey, string canonical)
        {
            if (!ReferenceEquals(scope, actualScope) || context != actualContext || key != actualKey || canonical != Canonical
                || context.CommandId != Guid.Empty || context.EntityId != reply.MessageId(context.Machine)
                || key != "turn-reply/v1/" + context.EntityId["turn-reply/".Length..]
                || context.ExpectedVersion != 0 || context.Epoch != reply.Epoch) throw new ArgumentException("Invalid turn reply admission.");
        }
        internal string Fingerprint(string raw) => TurnReplyFingerprint.Encode(context.Machine, reply.Source.SessionId, context.EntityId,
            new(reply.Origin, reply.Source.TerminalSeq, RelayHash.Sha(RelayHash.Utf8.GetBytes(reply.Source.ChatRunId)), raw));
        internal CommandResult Transition()
        {
            CommandResult Refuse(string error) => Rejected(context, error);
            var source = reply.Source;
            var child = scope.QueryFirst("""
                SELECT machine,holder,epoch,reserved_epoch,parent_id,state,provider_session_id,run_id FROM engine_session WHERE id=$child
                """, r => new
            {
                Machine = r.Text(0),
                Holder = r.Text(1),
                Epoch = r.Long(2),
                Reserved = !r.IsNull(3),
                Parent = r.IsNull(4) ? null : r.Text(4),
                State = r.Text(5),
                Acp = r.IsNull(6) ? null : r.Text(6),
                Run = r.IsNull(7) ? null : r.Text(7)
            }, new SqlArg("$child", source.SessionId));
            if (child is null || child.Machine != context.Machine || child.Holder != "local" || child.State == "foreign") return Refuse("not_local");
            if (child.Epoch != reply.Epoch) return Refuse("stale_epoch");
            if (child.Reserved || child.State == "handing_off") return Refuse("handoff_reserved");
            if (child.Parent is null) return Refuse("bad_args");
            if (RowStore.TerminalReplyAt(scope, source.SessionId, source.ChatRunId, source.TerminalSeq) != source) return Refuse("turn_reply_changed");
            if (reply.Observed is { } observed)
            {
                if (observed.StepId is { } step)
                {
                    if (observed.RunId is null || observed.Attempt is null || scope.QueryFirst("""
                        SELECT COUNT(*) FROM engine_step WHERE id=$step AND run_id=$run AND session_id=$session AND attempt=$attempt
                        """, r => r.Long(0), new SqlArg("$step", step), new SqlArg("$run", observed.RunId), new SqlArg("$session", source.SessionId), new SqlArg("$attempt", observed.Attempt)) != 1)
                        return Refuse("attribution_mismatch");
                }
                else if (observed.Attempt is not null || observed.RunId != child.Run) return Refuse("attribution_mismatch");
                if (reply.Final?.AcpSessionId is { } acp && child.Acp is not null && acp != child.Acp) return Refuse("attribution_mismatch");
            }
            else if (reply.Recovered is { } recovered)
            {
                if (reply.RecoveryMachine != context.Machine || scope.QueryFirst("""
                    SELECT COUNT(*) FROM session_info WHERE session_id=$child AND helper=1 AND COALESCE(machine,'')='' AND parent_session_id=$parent
                    """, r => r.Long(0), new SqlArg("$child", source.SessionId), new SqlArg("$parent", child.Parent)) != 1) return Refuse("not_local");
                if (scope.QueryFirst("""
                    SELECT COUNT(*) FROM rows WHERE session_id=$child AND seq=$seq AND run_id=$run AND kind='turn' AND json_extract(payload,'$.state')='started'
                    """, r => r.Long(0), new SqlArg("$child", source.SessionId), new SqlArg("$seq", recovered.StartedTurnSeq), new SqlArg("$run", source.ChatRunId)) != 1
                    || source.ErrorSeq is null || source.ErrorSeq <= recovered.SavedThrough || scope.QueryFirst("""
                    SELECT COUNT(*) FROM rows WHERE session_id=$child AND seq=$seq AND kind='error' AND run_id=$run AND created_at=$at AND json_extract(payload,'$.code')='backend_stopped'
                    """, r => r.Long(0), new SqlArg("$child", source.SessionId), new SqlArg("$seq", source.ErrorSeq), new SqlArg("$run", source.ChatRunId), new SqlArg("$at", recovered.EndedAt)) != 1)
                    return Refuse("turn_reply_changed");
            }
            return MessageTransitions.TurnReply(scope, context, source.SessionId, child.Parent, reply, policy);
        }
    }
}
