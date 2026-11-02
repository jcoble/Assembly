using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Providers;

internal static class ProviderCheckTransition
{
    internal static CommandResult Apply(IWriteScope scope, CommandContext context, PreparedProviderCheck prepared,
        Func<string, string, bool>? configured)
    {
        if (context.Actor.Kind != ActorKind.System || context.Actor.Id != "provider.effects") return Reject(context, "forbidden");
        var command = prepared.Command;
        if (context.CommandId == Guid.Empty || context.Epoch != 0 || context.ExpectedVersion < 0 ||
            command.Probe.TargetId != context.EntityId || command.Probe.ProviderVersion != context.ExpectedVersion)
            return Reject(context, "bad_args");
        var provider = ProviderState.Read(scope, context.EntityId);
        if (provider is null || provider.Machine != context.Machine || provider.Version != context.ExpectedVersion)
            return Reject(context, "version_conflict");
        if (ConfiguredProviderTarget.Resolve(context.Machine, provider.Id, provider, configured) is null) return Reject(context, "unavailable");
        var probe = ProviderProbe.Read(scope, provider, command.Probe.EffectId, command.Probe.ClaimAttempt);
        if (probe is null) return Reject(context, "stale_probe");
        var hasFacts = command.Facts.ValueKind != JsonValueKind.Null;
        if (hasFacts && command.Facts.GetProperty("provider").GetString() != provider.Target.Provider) return Reject(context, "bad_args");
        var preservePositive = command.AuthState is "unverified" or "could_not_check" &&
            (provider.AuthState == "signed_in" || probe.Payload.PriorAuthState == "signed_in" || ProviderProbe.HasPositiveFacts(scope, provider.Id));
        var auth = preservePositive ? "signed_in" : command.AuthState;
        var projection = auth switch { "signed_in" => "signedIn", "needs_sign_in" => "needsSignIn", _ => (string?)null };
        // json_set is restricted to the prescribed authoritative auth projection; ordinary facts retain exact UTF8 bytes.
        scope.Execute("""
            UPDATE engine_provider SET version=version+1,updated_ms=$now,installation=$installation,auth_state=$auth,
                reason=$reason,checked_ms=$now,
                facts_json=CASE WHEN $hasFacts=0 THEN CASE
                    WHEN facts_json IS NOT NULL AND $projection IS NOT NULL AND json_extract(facts_json,'$.auth') IS NOT $projection
                        THEN json_set(facts_json,'$.auth',$projection) ELSE facts_json END
                    WHEN $merge=1 THEN json_set(CAST($facts AS TEXT),'$.auth','signedIn') ELSE CAST($facts AS TEXT) END
            WHERE id=$id
            """, new("$now", context.NowMs), new("$installation", command.Installation), new("$auth", auth),
            new("$reason", command.Reason), new("$hasFacts", hasFacts ? 1 : 0), new("$merge", preservePositive ? 1 : 0),
            new("$facts", prepared.FactsUtf8), new("$projection", projection), new("$id", provider.Id));
        return EngineJournal.Commit(scope, context, provider.Version + 1, 0,
            [ProviderState.Changed(provider.Id, provider.Version + 1, auth, command.Reason)], []);
    }

    private static CommandResult Reject(CommandContext context, string error) =>
        new(false, false, error, context.ExpectedVersion, 0, context.Epoch);
}
