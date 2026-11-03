using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Projects;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Sessions;

public static partial class SessionTransitions
{
    internal static void QueueInitialCheckout(IWriteScope scope, CommandContext context, CheckoutIntent intent,
        IList<EventDraft> events, IList<EffectDraft> effects)
    {
        if (!(intent.Kind == "current" && intent.Branch is null && intent.Base is null ||
            intent.Kind == "branch" && !string.IsNullOrWhiteSpace(intent.Branch) && intent.Base is null ||
            intent.Kind == "worktree" && intent.Branch is null && !string.IsNullOrWhiteSpace(intent.Base)))
            throw new ArgumentException("Invalid initial checkout intent.");
        var start = effects.Single(effect => effect.Kind == "start" && effect.SessionId == context.EntityId);
        var payload = start.Payload.Deserialize(EngineJson.Default.SessionStartEffect)
            ?? throw new InvalidOperationException("Missing initial start payload.");
        var request = new ChangeCheckout(intent.Kind, intent.Branch, intent.Base, null);
        if (intent.Base is { } basis && (basis.Length > 256 || basis.Any(char.IsControl)) || intent.Kind != "worktree" && !ValidCheckout(request)) throw new ArgumentException("Invalid initial checkout intent.");
        effects.Remove(start);
        var operation = new CheckoutOperation(context.CommandId.ToString("D"), context.Epoch, payload.Folder, null, null, request, payload, context.CommandId.ToString("D"));
        StoreCheckout(scope, context, operation);
        events.Add(new("session.checkout_requested", "session", context.EntityId, context.ExpectedVersion + 1, false,
            new(context.EntityId, payload.RunId, null, payload.ParentId, "starting", context.Epoch)));
        effects.Add(CheckoutDraft(context, operation, payload.RunId));
    }

    private static bool ValidCheckout(ChangeCheckout request) =>
        request.CheckoutKind is "current" or "branch" or "worktree" or "folder" &&
        (request.CheckoutKind is "current" or "folder" || !string.IsNullOrWhiteSpace(request.Branch)) &&
        (request.CheckoutKind != "folder" || !string.IsNullOrWhiteSpace(request.Folder)) &&
        (request.Branch is null || request.Branch.Length <= 256 && !request.Branch.Any(char.IsControl)) &&
        (request.Base is null || request.Base.Length <= 256 && !request.Base.StartsWith('-') && !request.Base.Any(char.IsControl)) &&
        (request.Folder is null || request.Folder.Length <= 16384 && !request.Folder.Any(char.IsControl)) &&
        (request.ExpectedHead is null || request.ExpectedHead.Length is 40 or 64 && request.ExpectedHead.All(char.IsAsciiHexDigit));

    private static CommandResult Change(IWriteScope scope, CommandContext context, Session current, ChangeCheckout request)
    {
        if (context.Actor.Kind != ActorKind.Window) return Refused(context, "forbidden", current);
        var previous = scope.QueryFirst("SELECT checkout_intent_json FROM engine_session WHERE id=$id",
            row => row.IsNull(0) ? null : row.Text(0), new SqlArg("$id", context.EntityId));
        var accepted = previous is null ? null : JsonSerializer.Deserialize(previous, EngineJson.Default.CheckoutOperation);
        var initial = current.State == "starting" ? accepted?.InitialStart : null;
        if (request.CheckoutKind == "current")
        {
            var refused = scope.QueryFirst("SELECT checkout_state FROM engine_session WHERE id=$id", r => r.Text(0), new SqlArg("$id", context.EntityId)) == "refused";
            if (current.State != "starting" || !refused || accepted is null || accepted.Request?.CheckoutKind != "current" || initial is null ||
                request.Branch is not null || request.Base is not null || request.Folder is not null || request.CreateBranch || request.ExpectedHead is not null ||
                !VerifiedInitialCurrent(scope, context.EntityId, accepted)) return Refused(context, "bad_args", current);
        }
        if (request.Branch is { } branch && (branch.Length > 256 || branch.Any(char.IsControl)) ||
            request.Base is { } basis && (basis.Length > 256 || basis.Any(char.IsControl))) return Refused(context, "bad_args", current);
        var initialShape = initial is not null && request.Folder is null &&
            (request.CheckoutKind == "current" && request.Branch is null && request.Base is null ||
             request.CheckoutKind == "branch" && !string.IsNullOrWhiteSpace(request.Branch) && request.Base is null ||
             request.CheckoutKind == "worktree" && request.Branch is null && !string.IsNullOrWhiteSpace(request.Base));
        if (!initialShape && (request.CheckoutKind == "current" || !ValidCheckout(request))) return Refused(context, "bad_args", current);
        if (current.State is not ("idle" or "ended") && !(current.State == "starting" && initial is not null) || current.CancelRequested || current.ReservedEpoch is not null ||
            !CheckoutQuiescent(scope, context.EntityId)) return Refused(context, "busy", current);
        var source = scope.QueryFirst("""
            SELECT folder,branch,provider_session_id,holder,checkout_state FROM engine_session WHERE id=$id
            """, row => new
        {
            Folder = row.Text(0),
            Branch = row.IsNull(1) ? null : row.Text(1),
            Native = row.IsNull(2) ? null : row.Text(2),
            Holder = row.Text(3),
            Checkout = row.Text(4)
        }, new SqlArg("$id", context.EntityId))!;
        if (source.Holder != "local") return Refused(context, "writer_elsewhere", current);
        if (source.Checkout == "pending") return Refused(context, "checkout_pending", current);
        if (request.CheckoutKind == "branch" && scope.QueryFirst("""
            SELECT EXISTS(SELECT 1 FROM engine_session peer WHERE peer.folder=$folder AND peer.id<>$id AND peer.machine=$machine AND peer.holder='local'
              AND (peer.state NOT IN ('idle','ended') OR peer.cancel_requested<>0 OR peer.checkout_state='pending'
                OR EXISTS(SELECT 1 FROM engine_message m WHERE m.to_session_id=peer.id AND m.in_flight=1)
                OR EXISTS(SELECT 1 FROM engine_needs_me n WHERE n.session_id=peer.id AND n.state='open')))
            """, row => row.Long(0) != 0, new SqlArg("$folder", source.Folder), new("$id", context.EntityId), new("$machine", context.Machine)))
            return Refused(context, "checkout_busy", current);
        var operation = new CheckoutOperation(context.CommandId.ToString("D"), context.Epoch, source.Folder, source.Branch, source.Native, request, initial, accepted?.InitialOperationId);
        StoreCheckout(scope, context, operation);
        scope.Execute("UPDATE engine_session SET version=version+1,updated_ms=$now WHERE id=$id", new("$now", context.NowMs), new("$id", context.EntityId));
        return Commit(scope, context, current with { Version = current.Version + 1 }, "session.checkout_requested",
            [CheckoutDraft(context, operation, current.RunId)]);
    }

    private static bool VerifiedInitialCurrent(IReadScope scope, string session, CheckoutOperation accepted)
    {
        if (!Guid.TryParseExact(accepted.InitialOperationId, "D", out var initialId) || accepted.InitialStart is null) return false;
        var originalJson = scope.QueryFirst("""
            SELECT o.payload_json FROM engine_outbox o JOIN engine_command_receipt c ON c.command_id=$receipt
            WHERE o.id=$effect AND o.kind='checkout' AND o.session_id=$session
              AND json_extract(o.payload_json,'$.operationId')=$operation
              AND json_extract(o.payload_json,'$.initialOperationId')=$operation
              AND json_extract(c.result_json,'$.applied')=1
            """, row => row.Text(0), new SqlArg("$receipt", initialId.ToString("N")), new("$effect", "checkout/" + initialId.ToString("D")),
            new("$session", session), new("$operation", initialId.ToString("D")));
        if (originalJson is null) return false;
        var original = JsonSerializer.Deserialize(originalJson, EngineJson.Default.CheckoutOperation);
        return original?.Request?.CheckoutKind == "current" && original.InitialStart == accepted.InitialStart &&
            original.Folder == accepted.Folder && accepted.Folder == accepted.InitialStart.Folder;
    }

    internal static bool CheckoutQuiescent(IReadScope scope, string session) => scope.QueryFirst("""
        SELECT NOT EXISTS(SELECT 1 FROM engine_needs_me WHERE session_id=$id AND state='open')
          AND NOT EXISTS(SELECT 1 FROM engine_message WHERE to_session_id=$id AND in_flight=1)
          AND NOT EXISTS(SELECT 1 FROM engine_tool_call WHERE session_id=$id AND ended_ms IS NULL)
          AND NOT EXISTS(SELECT 1 FROM engine_step WHERE session_id=$id AND state IN ('running','waiting'))
          AND NOT EXISTS(SELECT 1 FROM engine_run WHERE owner_session_id=$id AND state NOT IN ('succeeded','cancelled','failed'))
          AND NOT EXISTS(SELECT 1 FROM engine_session WHERE parent_id=$id AND state NOT IN ('ended','moved'))
          AND NOT EXISTS(SELECT 1 FROM engine_outbox WHERE session_id=$id AND state NOT IN ('done','failed','dead'))
        """, row => row.Long(0) != 0, new SqlArg("$id", session));

    private static void StoreCheckout(IWriteScope scope, CommandContext context, CheckoutOperation operation) =>
        scope.Execute("""
            UPDATE engine_session SET checkout_state='pending',checkout_operation_id=$operation,checkout_intent_json=$intent WHERE id=$id
            """, new("$operation", operation.OperationId), new("$intent", JsonSerializer.Serialize(operation, EngineJson.Default.CheckoutOperation)), new("$id", context.EntityId));

    private static EffectDraft CheckoutDraft(CommandContext context, CheckoutOperation operation, string? run) =>
        new("checkout/" + operation.OperationId, "checkout", context.EntityId, run,
            JsonSerializer.SerializeToElement(operation, EngineJson.Default.CheckoutOperation), context.EntityId);

    internal static bool ValidCheckoutOutcome(IReadScope scope, Effect effect, EffectReceipt receipt)
    {
        var intent = effect.Payload.Deserialize(EngineJson.Default.CheckoutOperation);
        var result = receipt.Data.Deserialize(EngineJson.Default.CheckoutEffectData);
        return intent is not null && intent.Request is not null && result is not null && effect.Id == "checkout/" + intent.OperationId &&
            result.ProviderSessionId == intent.ProviderSessionId && !string.IsNullOrWhiteSpace(result.Folder) &&
            (!result.Confirmed || (result.Folder == intent.Folder || intent.InitialStart is not null) &&
                (intent.Request.CheckoutKind == "current" || intent.Request.CheckoutKind == "worktree" &&
                    result.Branch == "assembly-" + Guid.ParseExact(intent.OperationId, "D").ToString("N") || result.Branch == intent.Request.Branch)) &&
            (result.Confirmed || result.Folder == intent.Folder && result.Branch == intent.Branch && !string.IsNullOrWhiteSpace(result.Reason)) &&
            scope.QueryFirst("""
                SELECT EXISTS(SELECT 1 FROM engine_session WHERE id=$id AND checkout_state='pending'
                  AND checkout_operation_id=$operation AND checkout_intent_json=$intent AND epoch=$epoch
                  AND holder='local' AND machine=(SELECT machine_id FROM engine_meta WHERE singleton=1))
                """, row => row.Long(0) != 0, new SqlArg("$id", effect.SessionId), new("$operation", intent.OperationId),
                new("$intent", effect.Payload.GetRawText()), new("$epoch", intent.Epoch));
    }

    internal static void CompleteCheckout(IWriteScope scope, CommandContext context, Effect effect, EffectReceipt receipt,
        ISessionProjectProjection? projection)
    {
        var intent = effect.Payload.Deserialize(EngineJson.Default.CheckoutOperation)!;
        var result = receipt.Data.Deserialize(EngineJson.Default.CheckoutEffectData)!;
        var current = Find(scope, effect.SessionId!) ?? throw new InvalidOperationException("Checkout session disappeared.");
        var version = current.Version + 1;
        scope.Execute("""
            UPDATE engine_session SET checkout_state=$state,folder=$folder,branch=$branch,version=$version,updated_ms=$now WHERE id=$id
            """, new("$state", result.Confirmed ? "ready" : "refused"), new("$folder", result.Confirmed ? result.Folder : intent.Folder),
            new("$branch", result.Confirmed ? result.Branch : intent.Branch), new("$version", version), new("$now", context.NowMs), new("$id", effect.SessionId));
        List<EventDraft> events = [new(result.Confirmed ? "session.checkout_changed" : "session.checkout_refused", "session", effect.SessionId!, version, false,
            new(effect.SessionId, current.RunId, null, current.ParentId, current.State, current.Epoch, result.Reason))];
        List<EffectDraft> effects = [];
        if (result.Confirmed)
        {
            projection?.CheckoutChanged(scope, effect.SessionId!, result.Folder, result.Branch);
            if (intent.InitialStart is { } start)
                effects.Add(new("start/checkout/" + intent.OperationId, "start", effect.SessionId, start.RunId,
                    JsonSerializer.SerializeToElement(start with { Folder = result.Folder }, EngineJson.Default.SessionStartEffect), effect.SessionId));
            if (result.Folder == intent.Folder && result.Branch != intent.Branch)
            {
                foreach (var peer in scope.Query("""
                    UPDATE engine_session SET branch=$branch,version=version+1,updated_ms=$now
                    WHERE folder=$folder AND id<>$id AND machine=$machine AND holder='local' AND state IN ('idle','ended') AND checkout_state<>'pending'
                    RETURNING id,version,epoch,state,parent_id,run_id
                    """, r => new { Id = r.Text(0), Version = r.Long(1), Epoch = r.Long(2), State = r.Text(3), Parent = r.IsNull(4) ? null : r.Text(4), Run = r.IsNull(5) ? null : r.Text(5) },
                    new SqlArg("$branch", result.Branch), new("$now", context.NowMs), new("$folder", result.Folder), new("$id", effect.SessionId), new("$machine", context.Machine)))
                {
                    projection?.CheckoutChanged(scope, peer.Id, result.Folder, result.Branch);
                    events.Add(new("session.checkout_changed", "session", peer.Id, peer.Version, false, new(peer.Id, peer.Run, null, peer.Parent, peer.State, peer.Epoch)));
                }
            }
        }
        EngineJournal.Commit(scope, context with { EntityId = effect.SessionId!, ExpectedVersion = current.Version, Epoch = current.Epoch }, version, current.Epoch, events, effects);
    }
}
