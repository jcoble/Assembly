using System.Text.Json;
using Assembly.Engine.Relays;
using Assembly.Engine.Messages;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;

namespace Assembly.Engine.Persistence;

public static partial class EngineJournal
{
    internal static CommandResult ApplyForwardedParentNeed(IWriteScope scope, string machine,
        PreparedForwardedParentNeed prepared, long nowMs, MessageExpiryPolicy expiryPolicy)
    {
        var descriptor = prepared.Descriptor;
        var key = ParentNeedCodec.Key(descriptor.Source, descriptor.Target);
        var context = new CommandContext(RelayReceiptKeys.ContextGuid(key), machine,
            new(ActorKind.Session, descriptor.Source.ParentSessionId), descriptor.Target.NeedId,
            descriptor.Target.NeedVersion, descriptor.Target.ChildEpoch, nowMs, new("mcp", null, null));
        return ExecuteCore(scope, context, key, prepared.Canonical, () => machine != descriptor.Target.ChildOwnerMachine
            ? Rejected(context, "wrong_machine") : NeedTransitions.ApplyForwardedParent(scope, context, prepared, expiryPolicy));
    }

    internal static NeedAnswerAck ReadForwardedParentNeed(IReadScope scope, string machine,
        NeedAnswerRequestDescriptor descriptor)
    {
        var identity = ParentNeedCodec.Identity(descriptor);
        if (machine != descriptor.Target.ChildOwnerMachine) throw new RelayRefused("wrong_machine");
        var key = ParentNeedCodec.Key(descriptor.Source, descriptor.Target);
        var context = new CommandContext(RelayReceiptKeys.ContextGuid(key), machine,
            new(ActorKind.Session, descriptor.Source.ParentSessionId), descriptor.Target.NeedId,
            descriptor.Target.NeedVersion, descriptor.Target.ChildEpoch, 0);
        var canonical = "{\"kind\":\"parent_need\",\"parentNeedJournalVersion\":1,\"identitySha256\":\"" + identity + "\"}";
        var receipt = scope.QueryFirst("SELECT fingerprint,result_json FROM engine_command_receipt WHERE command_id=$key",
            row => new { Fingerprint = row.Text(0), Result = row.Text(1) }, new SqlArg("$key", key));
        if (receipt is null) return new("missing", identity, null, null, null);
        if (receipt.Fingerprint != Fingerprint(context, canonical)) throw new RelayRefused("source_changed");
        var result = JsonSerializer.Deserialize(receipt.Result, EngineJson.Default.CommandResult)!;
        var effectId = "answer/" + descriptor.Target.NeedId;
        var effect = result.Applied && descriptor.Target.ProviderBacked && descriptor.Source.Action == ParentNeedAction.Answer
            ? scope.QueryFirst("SELECT state FROM engine_outbox WHERE id=$id AND kind='answer' AND session_id=$session",
                row => row.Text(0), new SqlArg("$id", effectId), new("$session", descriptor.Target.ChildSessionId)) : null;
        return new(result.Applied ? "recorded" : "refused", identity, result, effect is null ? null : effectId, effect);
    }

    internal static ParentNeedIntentAccepted BeginParentNeedIntent(IWriteScope scope, string machine,
        PreparedParentNeedIntent prepared, long nowMs)
    {
        var source = prepared.Payload.Source;
        var context = new CommandContext(RelayReceiptKeys.ContextGuid(prepared.SourceKey), machine,
            new(ActorKind.Session, source.ParentSessionId), source.NeedId, 0, source.ParentEpoch, nowMs);
        var intentId = context.CommandId.ToString("N") + "-0";
        var result = ExecuteCore(scope, context, prepared.SourceKey, prepared.Canonical, () =>
        {
            if (source.SourceMachine != machine) return Rejected(context, "wrong_machine");
            if (ParentNeedParentRefusal(scope, source) is { } error) return Rejected(context, error);
            return Commit(scope, context, 0, source.ParentEpoch,
                [new("parent_need.requested", "effect", intentId, 0, false,
                    new(source.ParentSessionId, null, null, null, "pending", source.ParentEpoch))],
                [new(intentId, "parent_need", null, null, prepared.RawPayload)]);
        });
        return new(intentId, result);
    }
    internal static string ParentNeedPhaseFingerprint(PreparedParentNeedPhase prepared)
    {
        var source = prepared.Source;
        var context = new CommandContext(RelayReceiptKeys.ContextGuid(prepared.Key), source.SourceMachine,
            new(ActorKind.Session, source.ParentSessionId), source.NeedId, 0, source.ParentEpoch, 0);
        return Fingerprint(context, prepared.Canonical);
    }

    internal static CommandResult ApplyParentNeedPhase(IWriteScope scope, string machine,
        PreparedParentNeedPhase prepared, long nowMs)
    {
        var source = prepared.Source;
        var context = new CommandContext(RelayReceiptKeys.ContextGuid(prepared.Key), machine,
            new(ActorKind.Session, source.ParentSessionId), source.NeedId, 0, source.ParentEpoch, nowMs);
        return ExecuteCore(scope, context, prepared.Key, prepared.Canonical, () =>
        {
            if (machine != source.SourceMachine) return Rejected(context, "wrong_machine");
            var actual = scope.QueryFirst("""
                SELECT version,state,json_extract(payload_json,'$.format'),json_extract(payload_json,'$.source'),
                    json_extract(payload_json,'$.inputSha256'),json_extract(payload_json,'$.phase'),
                    json_extract(payload_json,'$.prepared.descriptor')
                FROM engine_outbox WHERE id=$id AND machine=$machine AND kind='parent_need'
                    AND session_id IS NULL AND run_id IS NULL
                """, r => new
            {
                Version = r.Long(0),
                State = r.Text(1),
                Format = r.Long(2),
                Source = r.Text(3),
                Input = r.Text(4),
                Phase = (ParentNeedPhase)r.Long(5),
                Descriptor = r.IsNull(6) ? null : r.Text(6)
            },
                new SqlArg("$id", prepared.IntentId), new("$machine", machine));
            if (actual is null || actual.Format != 1 || actual.Version != prepared.ExpectedVersion ||
                actual.State != prepared.ExpectedState || actual.Phase != prepared.ExpectedPhase ||
                actual.Source != prepared.SourceJson || actual.Input != prepared.InputSha256 ||
                actual.Descriptor != prepared.ExpectedDescriptorJson) return Rejected(context, "source_changed");
            var preparing = prepared.Write == ParentNeedPhaseWrite.Prepare;
            var dispatching = prepared.Write == ParentNeedPhaseWrite.Dispatch;
            if (preparing && actual.Phase != ParentNeedPhase.Raw || dispatching && actual.Phase != ParentNeedPhase.Prepared)
                return Rejected(context, "invalid_transition");
            if (preparing || dispatching)
            {
                if (ParentNeedParentRefusal(scope, source) is { } error) return Rejected(context, error);
                if (ParentNeedChildRefusal(scope, source, prepared.Descriptor!.Target) is { } childError)
                    return Rejected(context, childError);
            }
            var outcome = prepared.Outcome;
            if (prepared.Write == ParentNeedPhaseWrite.Outcome)
            {
                if (outcome!.Origin == "source")
                {
                    var error = ParentNeedParentRefusal(scope, source) ?? (prepared.Descriptor is { } descriptor
                        ? ParentNeedChildRefusal(scope, source, descriptor.Target) : null);
                    var refusedPreparation = false;
                    if (actual.Phase == ParentNeedPhase.Raw && prepared.RejectedPrepareFingerprint is { } expectedFingerprint)
                    {
                        var receipt = scope.QueryFirst("SELECT fingerprint,result_json FROM engine_command_receipt WHERE command_id=$key",
                            row => new { Fingerprint = row.Text(0), Result = row.Text(1) },
                            new SqlArg("$key", ParentNeedCodec.SourceKey(source) + "/prepare"));
                        refusedPreparation = receipt is not null && receipt.Fingerprint == expectedFingerprint &&
                            JsonSerializer.Deserialize(receipt.Result, EngineJson.Default.CommandResult) is { Applied: false, Error: not null } refusal &&
                            refusal.Error == outcome.Error;
                    }
                    if (actual.Phase is not (ParentNeedPhase.Raw or ParentNeedPhase.Prepared) ||
                        !refusedPreparation && (error is null ? outcome.Error != "bad_args" : outcome.Error != error))
                        return Rejected(context, "forbidden");
                }
                else if (actual.Phase != ParentNeedPhase.Executing) return Rejected(context, "invalid_transition");
            }
            var state = preparing ? "pending" : dispatching || outcome!.Status == "unknown" ? "uncertain"
                : outcome.Status == "recorded" ? "done" : "failed";
            scope.Execute("""
                UPDATE engine_outbox SET version=version+1,state=$state,payload_json=$payload,updated_ms=$now,
                    attempt=attempt+$dispatch WHERE id=$id
                """, new("$state", state), new("$payload", prepared.NextJson), new("$now", nowMs),
                new("$dispatch", dispatching ? 1 : 0), new("$id", prepared.IntentId));
            return Commit(scope, context, actual.Version + 1, source.ParentEpoch,
                [new("parent_need.changed", "effect", prepared.IntentId, actual.Version + 1, false,
                    new(source.ParentSessionId, null, null, null, state, source.ParentEpoch))], []);
        });
    }

    private static string? ParentNeedChildRefusal(IReadScope scope, ParentNeedSource source, NeedAnswerTarget target)
    {
        var child = scope.QueryFirst("SELECT machine,state,parent_id,holder FROM engine_session WHERE id=$id",
            row => new { Machine = row.Text(0), State = row.Text(1), Parent = row.IsNull(2) ? null : row.Text(2), Holder = row.Text(3) },
            new SqlArg("$id", target.ChildSessionId));
        return child is not null && (child.Machine != target.ChildOwnerMachine || child.State != "foreign" ||
            child.Parent != source.ParentSessionId || child.Holder != target.ChildOwnerMachine) ? "forbidden" : null;
    }

    private static string? ParentNeedParentRefusal(IReadScope scope, ParentNeedSource source)
    {
        var parent = scope.QueryFirst("""
            SELECT epoch,state,holder,reserved_epoch,parent_id FROM engine_session WHERE id=$id AND machine=$machine
            """, r => new { Epoch = r.Long(0), State = r.Text(1), Holder = r.Text(2), Reserved = !r.IsNull(3), HasParent = !r.IsNull(4) },
            new SqlArg("$id", source.ParentSessionId), new("$machine", source.SourceMachine));
        if (parent is null || parent.State is "foreign" or "lost" or "ended") return "session_gone";
        if (parent.Epoch != source.ParentEpoch) return "stale_epoch";
        if (parent.State == "handing_off") return "handing_off";
        if (parent.Holder != "local" || parent.Reserved) return "forbidden";
        return source.Action == ParentNeedAction.Escalate && parent.HasParent ? "owner_reserved" : null;
    }

}
