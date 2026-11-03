using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Providers;

internal sealed partial record AuthNeed
{
    private sealed record ClosureNeed(string Id, long Version, string Session, long Epoch, string? Run, string? Step);

    internal static IReadOnlyList<EventDraft> SignedIn(IWriteScope scope, CommandContext context, string operationId,
        long operationVersion, ConfiguredProviderTarget target, IList<EffectDraft> effects)
    {
        if (scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_sign_in WHERE id=$id AND version=$version AND machine=$machine
                AND provider=$provider AND account_context=$account AND state='signed_in'
            """, r => r.Long(0), new SqlArg("$id", operationId), new("$version", operationVersion), new("$machine", context.Machine),
            new("$provider", target.Provider), new("$account", target.AccountContext)) != 1)
            throw new InvalidOperationException("The checked sign-in terminal changed.");
        List<EventDraft> events = [];
        Dictionary<string, long> sessions = new(StringComparer.Ordinal); HashSet<string> runs = new(StringComparer.Ordinal);
        string? after = null;
        while (true)
        {
            var page = scope.Query("""
                SELECT n.id,n.version,n.session_id,se.epoch,n.run_id,n.step_id FROM engine_needs_me n
                JOIN engine_session se ON se.id=n.session_id
                WHERE n.machine=$machine AND se.machine=$machine AND se.provider=$provider AND se.account_context=$account
                  AND se.holder='local' AND se.reserved_epoch IS NULL AND se.state NOT IN ('foreign','handing_off','ended')
                  AND n.state='open' AND n.kind='confirmation' AND n.provider_request_id IS NULL AND n.chat_row_id IS NULL
                  AND (CAST(n.text AS BLOB)=$text OR CAST(n.text AS BLOB)=$inserted)
                  AND json_type(n.options_json)='array' AND json_array_length(n.options_json)=2
                  AND json_extract(n.options_json,'$[0]')='sign_in' AND json_extract(n.options_json,'$[1]')='dismiss'
                  AND ($after IS NULL OR n.id>$after) ORDER BY n.id LIMIT 100
                """, r => new ClosureNeed(r.Text(0), r.Long(1), r.Text(2), r.Long(3), r.IsNull(4) ? null : r.Text(4), r.IsNull(5) ? null : r.Text(5)),
                new SqlArg("$machine", context.Machine), new("$provider", target.Provider), new("$account", target.AccountContext),
                new("$text", Encoding.UTF8.GetBytes(Copy(target.Provider, context.Machine))), new("$inserted", Encoding.UTF8.GetBytes(InsertionCopy(target.Provider, context.Machine))), new("$after", after));
            if (page.Count == 0) break;
            after = page[^1].Id;
            var valid = page.Where(n => ValidId(n.Id, n.Session)).ToDictionary(n => n.Id, StringComparer.Ordinal);
            if (valid.Count == 0) continue;
            IReadOnlyList<string> ids = valid.Keys.ToArray();
            var changed = scope.Query("""
                UPDATE engine_needs_me SET state='answered',version=version+1,updated_ms=$now,
                    answer_json='"signed_in"',answered_by=$actor,answered_ms=$now,answered_origin_json=$origin
                WHERE state='open' AND id IN (SELECT value FROM json_each($ids)) RETURNING id,version
                """, r => (Id: r.Text(0), Version: r.Long(1)), new SqlArg("$now", context.NowMs), new("$actor", "sign-in/" + operationId),
                new("$origin", NeedTransitions.OriginJson(context)),
                new("$ids", JsonSerializer.Serialize(ids, EngineJson.Default.IReadOnlyListString)));
            if (changed.Count != valid.Count) throw new InvalidOperationException("The matching auth needs changed.");
            foreach (var closed in changed)
            {
                var need = valid[closed.Id];
                if (closed.Version != need.Version + 1) throw new InvalidOperationException("The matching auth need version changed.");
                events.Add(new("needs_me.answered", "needs_me", need.Id, closed.Version, false,
                    new(need.Session, need.Run, need.Step, null, "answered", need.Epoch, "signed_in")));
                sessions[need.Session] = need.Epoch; if (need.Run is not null) runs.Add(need.Run);
            }
        }
        // Close the entire matching set before attempting one ordinary release for each owning session.
        foreach (var session in sessions.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var local = context with { EntityId = session.Key, Epoch = session.Value };
            events.AddRange(SessionTransitions.RecomputeNeeds(scope, local, session.Key));
            events.AddRange(MessageRelease.Next(scope, local, session.Key, effects));
        }
        foreach (var run in runs.Order(StringComparer.Ordinal)) events.AddRange(RunTransitions.Recompute(scope, context, run));
        return events;
    }
}
