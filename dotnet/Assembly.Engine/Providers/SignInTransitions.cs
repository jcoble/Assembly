using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Providers;

internal static class SignInTransitions
{
    internal const string ActiveStates = "('starting','waiting_for_person','finishing')";
    private sealed record Operation(string Id, long Version, string Machine, ConfiguredProviderTarget Target,
        string? Method, string State, long Deadline, string? OriginNeed);

    internal static CommandResult Apply(IWriteScope scope, CommandContext context, SignInCommand command,
        Func<string, string, bool>? configured)
    {
        if (string.IsNullOrWhiteSpace(context.EntityId) || context.CommandId == Guid.Empty || context.ExpectedVersion < 0 || context.Epoch != 0)
            return Reject(context, "bad_args");
        var row = Read(scope, context.EntityId);
        if (command is StartSignIn start) return Start(scope, context, start, row, configured);
        if (row is null) return Reject(context, "bad_args");
        if (row.Machine != context.Machine) return Reject(context, "wrong_machine", row);
        if (row.Version != context.ExpectedVersion) return Reject(context, "version_conflict", row);
        var allowed = command switch
        {
            CancelSignIn => context.Actor.Kind == ActorKind.Window,
            ExpireSignIn => Fixed(context, "engine.worker"),
            InterruptSignIn => Fixed(context, "engine.initialize"),
            SignInProgress => Fixed(context, "sign_in.effects"),
            _ => false
        };
        if (!allowed) return Reject(context, "forbidden", row);
        if (!Active(row.State)) return Reject(context, "invalid_transition", row);
        if (command is ExpireSignIn && context.NowMs < row.Deadline) return Reject(context, "not_expired", row);
        return command switch
        {
            CancelSignIn => End(scope, context, row, "cancelled", null, cancel: true),
            ExpireSignIn => End(scope, context, row, "expired", null, cancel: true),
            InterruptSignIn => End(scope, context, row, "failed", "interrupted", cancel: false),
            SignInProgress progress => Progress(scope, context, row, progress, configured),
            _ => Reject(context, "unsupported_command", row)
        };
    }

    private static CommandResult Start(IWriteScope scope, CommandContext context, StartSignIn start,
        Operation? existing, Func<string, string, bool>? configured) => context.Actor.Kind != ActorKind.Window
        ? Reject(context, "forbidden", existing) : StartCore(scope, context, start, existing, configured);

    internal static CommandResult StartFromNeed(IWriteScope scope, CommandContext context, string needId,
        Func<string, string, bool>? configured)
    {
        var need = AuthNeed.Open(scope, context.Machine, needId);
        if (need is null || need.Version != context.ExpectedVersion || need.Epoch != context.Epoch)
            return Reject(context, "bad_args");
        if (context.Actor.Kind != ActorKind.Window && (context.Actor.Kind != ActorKind.Session ||
            need.ParentId != context.Actor.Id || need.Addressee != context.Actor.Id)) return Reject(context, "forbidden");
        var operationId = context.CommandId.ToString("N");
        var operationContext = context with { EntityId = operationId, ExpectedVersion = 0, Epoch = 0 };
        var result = StartCore(scope, operationContext, new(need.Target.Provider, need.Target.AccountContext, null, need.Id),
            Read(scope, operationId), configured);
        if (result.Error != "already_running") return result;
        // StartCore checked the exact configured pair and local provider before reporting this existing active target.
        var active = scope.QueryFirst($"SELECT version FROM engine_sign_in WHERE machine=$machine AND provider=$provider AND account_context=$account AND state IN {ActiveStates}",
            r => (long?)r.Long(0), new SqlArg("$machine", context.Machine), new("$provider", need.Target.Provider), new("$account", need.Target.AccountContext));
        return active is null ? result : new(true, false, null, active.Value, 0, 0);
    }

    private static CommandResult StartCore(IWriteScope scope, CommandContext context, StartSignIn start,
        Operation? existing, Func<string, string, bool>? configured)
    {
        if (existing is not null || context.ExpectedVersion != 0) return Reject(context, "version_conflict", existing);
        if (string.IsNullOrWhiteSpace(start.Provider) || !Assembly.Application.Common.Agents.AccountContext.IsValid(start.AccountContext) ||
            start.MethodId is not null && string.IsNullOrWhiteSpace(start.MethodId)) return Reject(context, "bad_args");
        var target = new ConfiguredProviderTarget(start.Provider, start.AccountContext);
        if (configured is null || !configured(target.Provider, target.AccountContext)) return Reject(context, "unavailable");
        var provider = ProviderState.Read(scope, target.Id);
        if (provider is not null && (provider.Machine != context.Machine || provider.Target != target)) return Reject(context, "unavailable");
        if (start.OriginNeedId is not null && !MatchingNeed(scope, context, target, start.OriginNeedId)) return Reject(context, "bad_args");
        if (scope.QueryFirst($"SELECT COUNT(*) FROM engine_sign_in WHERE machine=$machine AND provider=$provider AND account_context=$account AND state IN {ActiveStates}",
            r => r.Long(0), new SqlArg("$machine", context.Machine), new("$provider", target.Provider), new("$account", target.AccountContext)) != 0)
            return Reject(context, "already_running");
        if (provider?.AuthState == "signed_in")
        {
            var count = scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_session WHERE machine=$machine AND provider=$provider AND holder='local' AND state='running'
                """, r => r.Long(0), new SqlArg("$machine", context.Machine), new("$provider", target.Provider));
            if (count != 0) return Reject(context, "conflict_running:" + count);
        }
        List<EventDraft> events = [];
        if (provider is null) ProviderState.Create(scope, context, target, "unverified", events);
        var deadline = checked(context.NowMs + 300_000);
        scope.Execute("""
            INSERT INTO engine_sign_in(id,version,machine,created_ms,updated_ms,provider,account_context,method_id,state,deadline_ms,origin_need_id)
            VALUES($id,1,$machine,$now,$now,$provider,$account,$method,'starting',$deadline,$origin)
            """, new("$id", context.EntityId), new("$machine", context.Machine), new("$now", context.NowMs),
            new("$provider", target.Provider), new("$account", target.AccountContext), new("$method", start.MethodId),
            new("$deadline", deadline), new("$origin", start.OriginNeedId));
        events.Add(Changed(context.EntityId, 1, "starting"));
        var payload = JsonSerializer.SerializeToElement(new BeginSignInEffect(context.EntityId, target.Provider, start.MethodId), EngineJson.Default.BeginSignInEffect);
        return EngineJournal.Commit(scope, context, 1, 0, events,
            [new("begin_sign_in/" + context.EntityId, "begin_sign_in", null, null, payload, "provider/" + target.Id)]);
    }

    private static bool MatchingNeed(IReadScope scope, CommandContext context, ConfiguredProviderTarget target, string need) =>
        AuthNeed.Open(scope, context.Machine, need)?.Target == target;

    private static CommandResult Progress(IWriteScope scope, CommandContext context, Operation row,
        SignInProgress progress, Func<string, string, bool>? configured)
    {
        if (configured is null || !configured(row.Target.Provider, row.Target.AccountContext)) return Reject(context, "unavailable", row);
        if (!BoundBegin(scope, row, progress.Attempt)) return Reject(context, "stale_attempt", row);
        var valid = progress.State switch
        {
            "waiting_for_person" => row.State is "starting" or "waiting_for_person",
            "finishing" => row.State is "starting" or "waiting_for_person" or "finishing",
            "signed_in" => row.State == "finishing",
            "failed" => true,
            _ => false
        };
        if (!valid) return Reject(context, "invalid_transition", row);
        if (progress.Reason is not null && progress.Reason is not ("not_installed" or "cannot_start" or "invalid_descriptor" or
            "timed_out" or "adapter_crashed" or "network" or "version_unknown" or "interrupted" or "adapter_error" or "unsupported"))
            return Reject(context, "bad_args", row);
        return Update(scope, context, row, progress.State, progress.Reason, signedIn: progress.State == "signed_in");
    }

    private static bool BoundBegin(IReadScope scope, Operation row, SignInAttemptBinding binding)
    {
        if (binding.OperationId != row.Id || binding.TargetId != row.Target.Id || binding.BeginEffectId != "begin_sign_in/" + row.Id || binding.BeginClaimAttempt <= 0)
            return false;
        var effect = scope.QueryFirst("""
            SELECT machine,kind,session_id,run_id,payload_json,attempt,state,order_key FROM engine_outbox WHERE id=$id
            """, r => new
        {
            Machine = r.Text(0),
            Kind = r.Text(1),
            Sessionless = r.IsNull(2) && r.IsNull(3),
            Payload = r.Text(4),
            Attempt = r.Long(5),
            State = r.Text(6),
            Order = r.Text(7)
        }, new SqlArg("$id", binding.BeginEffectId));
        if (effect is null || effect.Machine != row.Machine || effect.Kind != "begin_sign_in" || !effect.Sessionless ||
            effect.Attempt != binding.BeginClaimAttempt || effect.State is not ("inflight" or "done") || effect.Order != "provider/" + row.Target.Id) return false;
        try
        {
            return JsonSerializer.Deserialize(effect.Payload, EngineJson.Default.BeginSignInEffect) == new BeginSignInEffect(row.Id, row.Target.Provider, row.Method);
        }
        catch (JsonException) { return false; }
    }

    private static CommandResult End(IWriteScope scope, CommandContext context, Operation row, string state, string? reason, bool cancel)
    {
        scope.Execute("""
            UPDATE engine_outbox SET state='failed',version=version+1,updated_ms=$now,last_error='interrupted'
            WHERE id=$id AND kind='begin_sign_in' AND state IN ('pending','unavailable') AND attempt=0
            """, new("$now", context.NowMs), new("$id", "begin_sign_in/" + row.Id));
        List<EffectDraft> effects = [];
        if (cancel) effects.Add(new("cancel_sign_in/" + row.Id, "cancel_sign_in", null, null,
            JsonSerializer.SerializeToElement(new CancelSignInEffect(row.Id), EngineJson.Default.CancelSignInEffect), "provider/" + row.Target.Id));
        return Update(scope, context, row, state, reason, effects: effects);
    }

    private static CommandResult Update(IWriteScope scope, CommandContext context, Operation row, string state,
        string? reason, bool signedIn = false, List<EffectDraft>? effects = null)
    {
        effects ??= [];
        scope.Execute("""
            UPDATE engine_sign_in SET version=version+1,state=$state,reason=$reason,updated_ms=$now WHERE id=$id
            """, new("$state", state), new("$reason", reason), new("$now", context.NowMs), new("$id", row.Id));
        List<EventDraft> events = [Changed(row.Id, row.Version + 1, state, reason)];
        if (signedIn)
        {
            var provider = ProviderState.Read(scope, row.Target.Id);
            if (provider is null || provider.Machine != row.Machine || provider.Target != row.Target)
                throw new InvalidOperationException("The sign-in provider target changed.");
            scope.Execute("""
                UPDATE engine_provider SET version=version+1,auth_state='signed_in',reason=NULL,updated_ms=$now,
                    facts_json=CASE WHEN facts_json IS NULL THEN NULL ELSE json_set(facts_json,'$.auth','signedIn') END WHERE id=$id
                """, new("$now", context.NowMs), new("$id", row.Target.Id));
            events.Add(ProviderState.Changed(row.Target.Id, provider.Version + 1, "signed_in"));
            events.AddRange(AuthNeed.SignedIn(scope, context, row.Id, row.Version + 1, row.Target, effects));
            effects.Add(new($"probe_provider/{row.Target.Id}/{provider.Version + 1}", "probe_provider", null, null,
                JsonSerializer.SerializeToElement(new ProbeProviderEffect(row.Target.Id, provider.Version + 1, "signed_in"), EngineJson.Default.ProbeProviderEffect), "provider/" + row.Target.Id));
        }
        return EngineJournal.Commit(scope, context, row.Version + 1, 0, events, effects);
    }

    private static Operation? Read(IReadScope scope, string id) => scope.QueryFirst("""
        SELECT id,version,machine,provider,account_context,method_id,state,deadline_ms,origin_need_id FROM engine_sign_in WHERE id=$id
        """, r => new Operation(r.Text(0), r.Long(1), r.Text(2), new(r.Text(3), r.Text(4)), r.IsNull(5) ? null : r.Text(5),
            r.Text(6), r.Long(7), r.IsNull(8) ? null : r.Text(8)), new SqlArg("$id", id));
    internal static bool Active(string state) => state is "starting" or "waiting_for_person" or "finishing";
    private static bool Fixed(CommandContext context, string actor) => context.Actor.Kind == ActorKind.System && context.Actor.Id == actor;
    private static EventDraft Changed(string id, long version, string state, string? reason = null) =>
        new("sign_in.changed", "sign_in", id, version, false, new(null, null, null, null, state, 0, reason));
    private static CommandResult Reject(CommandContext context, string error, Operation? row = null) => new(false, false, error, row?.Version ?? 0, 0, 0);
}
