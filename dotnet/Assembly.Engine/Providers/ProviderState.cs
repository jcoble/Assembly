using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Providers;

internal sealed record ConfiguredProviderTarget(string Provider, string AccountContext)
{
    internal string Id => Provider + "/" + AccountContext;

    internal static ConfiguredProviderTarget? Resolve(string machine, string id, ProviderState? stored,
        Func<string, string, bool>? configured)
    {
        if (configured is null) return null;
        if (stored is not null)
            return stored.Machine == machine && stored.Target.Id == id && Assembly.Application.Common.Agents.AccountContext.IsValid(stored.Target.AccountContext) && configured(stored.Target.Provider, stored.Target.AccountContext)
                ? stored.Target : null;
        ConfiguredProviderTarget? found = null;
        for (var index = 1; index < id.Length; index++)
        {
            if (id[index] != '/') continue;
            var provider = id[..index]; var account = id[(index + 1)..];
            if (!Assembly.Application.Common.Agents.AccountContext.IsValid(account) || !configured(provider, account)) continue;
            if (found is not null) return null;
            found = new(provider, account);
        }
        return found;
    }
}

internal sealed record ProviderState(string Id, long Version, string Machine, ConfiguredProviderTarget Target,
    string Installation, string AuthState, string? Reason, bool HasFacts)
{
    internal static ProviderState? Read(IReadScope scope, string id) => scope.QueryFirst("""
        SELECT id,version,machine,provider,account_context,installation,auth_state,reason,facts_json IS NOT NULL
        FROM engine_provider WHERE id=$id
        """, r => new ProviderState(r.Text(0), r.Long(1), r.Text(2), new(r.Text(3), r.Text(4)),
            r.Text(5), r.Text(6), r.IsNull(7) ? null : r.Text(7), r.Long(8) != 0), new SqlArg("$id", id));

    internal static ProviderState Create(IWriteScope scope, CommandContext context, ConfiguredProviderTarget target,
        string auth, List<EventDraft> events)
    {
        scope.Execute("""
            INSERT INTO engine_provider(id,version,machine,created_ms,updated_ms,provider,account_context,installation,auth_state)
            VALUES($id,1,$machine,$now,$now,$provider,$account,'unknown',$auth)
            """, new("$id", target.Id), new("$machine", context.Machine), new("$now", context.NowMs),
            new("$provider", target.Provider), new("$account", target.AccountContext), new("$auth", auth));
        events.Add(Changed(target.Id, 1, auth));
        return new(target.Id, 1, context.Machine, target, "unknown", auth, null, false);
    }

    internal static EventDraft Changed(string id, long version, string state, string? reason = null) =>
        new("provider.changed", "provider", id, version, false, new(null, null, null, null, state, 0, reason));
}
