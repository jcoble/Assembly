using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Providers;

internal static class ProviderTransitions
{
    internal static CommandResult Apply(IWriteScope scope, CommandContext context, ProviderCommand command,
        Func<string, string, bool>? configured)
    {
        if (context.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(context.EntityId) || context.ExpectedVersion < 0 || context.Epoch != 0)
            return Reject(context, "bad_args");
        var row = ProviderState.Read(scope, context.EntityId);
        if (row is not null && row.Machine != context.Machine) return Reject(context, "wrong_machine", row);
        if ((row?.Version ?? 0) != context.ExpectedVersion) return Reject(context, "version_conflict", row);
        if (command is InterruptProviderCheck)
        {
            if (context.Actor.Kind != ActorKind.System || context.Actor.Id != "engine.initialize") return Reject(context, "forbidden", row);
            if (row?.AuthState != "checking") return Reject(context, "invalid_transition", row);
            return Interrupt(scope, context, row);
        }
        if (command is not CheckProvider) return Reject(context, "forbidden", row);
        if (context.Actor.Kind is not (ActorKind.Window or ActorKind.System)) return Reject(context, "forbidden", row);
        var target = ConfiguredProviderTarget.Resolve(context.Machine, context.EntityId, row, configured);
        if (target is null) return Reject(context, "unavailable", row);
        if (row?.AuthState == "checking") return new(true, false, null, row.Version, 0, 0);
        var prior = row?.AuthState == "signed_in" ? "signed_in" : null;
        List<EventDraft> events = [];
        long version;
        if (row is null) version = ProviderState.Create(scope, context, target, "checking", events).Version;
        else
        {
            version = checked(row.Version + 1);
            scope.Execute("""
                UPDATE engine_provider SET version=version+1,auth_state='checking',reason=NULL,updated_ms=$now WHERE id=$id
                """, new("$now", context.NowMs), new("$id", row.Id));
            events.Add(ProviderState.Changed(row.Id, version, "checking"));
        }
        var intent = new ProbeProviderEffect(target.Id, version, prior);
        return EngineJournal.Commit(scope, context, version, 0, events,
            [new($"probe_provider/{target.Id}/{version}", "probe_provider", null, null,
                JsonSerializer.SerializeToElement(intent, EngineJson.Default.ProbeProviderEffect), "provider/" + target.Id)]);
    }

    private static CommandResult Interrupt(IWriteScope scope, CommandContext context, ProviderState row)
    {
        var probe = ProviderProbe.Read(scope, row, $"probe_provider/{row.Id}/{row.Version}");
        var auth = probe?.Payload.PriorAuthState == "signed_in" || ProviderProbe.HasPositiveFacts(scope, row.Id)
            ? "signed_in" : "could_not_check";
        scope.Execute("""
            UPDATE engine_provider SET version=version+1,auth_state=$auth,reason='interrupted',updated_ms=$now WHERE id=$id
            """, new("$auth", auth), new("$now", context.NowMs), new("$id", row.Id));
        return EngineJournal.Commit(scope, context, row.Version + 1, 0,
            [ProviderState.Changed(row.Id, row.Version + 1, auth, "interrupted")], []);
    }

    private static CommandResult Reject(CommandContext context, string error, ProviderState? row = null) =>
        new(false, false, error, row?.Version ?? 0, 0, 0);
}
