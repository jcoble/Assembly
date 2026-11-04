using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Workflows.Runs;

/// <summary>Undo token expiry shares the worker's single deadline wait and never changes run outcomes.</summary>
internal static class WorkflowControlDeadlines
{
    internal static int ExpireUndo(IWriteScope scope, string machine, long now)
    {
        var due = scope.Query("""
            SELECT r.id,r.version,r.owner_session_id,r.state,owner.epoch
            FROM engine_run r JOIN engine_session owner ON owner.id=r.owner_session_id
            WHERE r.machine=$machine AND r.keep_token IS NOT NULL AND r.keep_until_ms<=$now
            ORDER BY r.keep_until_ms,r.id LIMIT 100
            """, r => new { Id = r.Text(0), Version = r.Long(1), Owner = r.Text(2), State = r.Text(3), Epoch = r.Long(4) },
            new SqlArg("$machine", machine), new("$now", now));
        foreach (var row in due)
        {
            scope.Execute("""
                UPDATE engine_run SET keep_token=NULL,keep_until_ms=NULL,keep_version=NULL,version=version+1,updated_ms=$now WHERE id=$id
                """, new("$now", now), new("$id", row.Id));
            var context = new CommandContext(Guid.NewGuid(), machine, new(ActorKind.System, "engine.worker"), row.Id, row.Version, row.Epoch, now);
            EngineJournal.Commit(scope, context, row.Version + 1, row.Epoch,
                [new("workflow.keep_expired", "run", row.Id, row.Version + 1, false, new(row.Owner, row.Id, null, null, row.State, row.Epoch))], []);
        }
        return due.Count;
    }
}
