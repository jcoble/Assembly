using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Sessions;

public sealed record ImportedSession(string Id, string? ParentId, string Provider,
    string? Model, string Folder, string Title, string AccountContext);

public static class SessionImport
{
    public static int Import(IWriteScope scope, string machine, long nowMs, IReadOnlyList<ImportedSession> sessions)
    {
        var byId = sessions.ToDictionary(session => session.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var session in sessions) Add(session);
        return count;

        void Add(ImportedSession session)
        {
            if (visited.Contains(session.Id)) return;
            var existing = scope.QueryFirst("SELECT machine FROM engine_session WHERE id=$id", r => r.Text(0), new SqlArg("$id", session.Id));
            if (existing is not null)
            {
                if (existing != machine) throw new InvalidOperationException($"Session {session.Id} belongs to another machine.");
                visited.Add(session.Id);
                return;
            }
            if (!visiting.Add(session.Id)) throw new InvalidOperationException("Imported sessions contain a parent cycle.");
            if (string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.Provider) || string.IsNullOrWhiteSpace(session.Folder))
                throw new InvalidOperationException("Imported session identity, provider and folder are required.");
            if (session.ParentId is { } parent)
            {
                if (byId.TryGetValue(parent, out var ancestor)) Add(ancestor);
                else if (scope.QueryFirst("SELECT COUNT(*) FROM engine_session WHERE id=$id", r => r.Long(0), new SqlArg("$id", parent)) != 1)
                    throw new InvalidOperationException($"Parent session {parent} requires its owning session or authenticated foreign registration.");
            }
            scope.Execute("""
                INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,
                    account_context,state,activity_state,parent_id,holder,epoch,cancel_requested,managed_checkout)
                VALUES($id,1,$machine,$now,$now,$provider,$model,$folder,$title,$account,'idle','idle',$parent,'local',0,0,1)
                """, new("$id", session.Id), new("$machine", machine), new("$now", nowMs), new("$provider", session.Provider),
                new("$model", session.Model), new("$folder", session.Folder), new("$title", session.Title),
                new("$account", session.AccountContext), new("$parent", session.ParentId));
            var context = new CommandContext(Guid.NewGuid(), machine, new(ActorKind.System, "session.import"), session.Id, 0, 0, nowMs);
            EngineJournal.Commit(scope, context, 1, 0,
                [new("session.started", "session", session.Id, 1, false, new(session.Id, null, null, session.ParentId, "idle", 0))], []);
            visiting.Remove(session.Id); visited.Add(session.Id); count++;
        }
    }
}
