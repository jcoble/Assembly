using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Workflows.Reading;

public static partial class WorkflowQueries
{
    public static JsonElement History(IReadScope scope, string runId, string? after, int limit)
    {
        Limit(limit);
        var machine = Machine(scope);
        RequireRun(scope, runId, machine);
        var cursor = after is null ? null : WorkflowReadModels.ReadCursor(after, WorkflowReadJson.Default.WorkflowHistoryReadCursor);
        if (cursor is not null && (cursor.Machine != machine || cursor.RunId != runId || cursor.HistorySeq < 0))
            throw new ArgumentException("invalid_cursor");
        var rows = scope.Query("""
            WITH candidates AS (
              SELECT h.id,h.history_seq,json_object('id',h.id,'version',h.version,'machine',h.machine,
                'runId',h.run_id,'historySeq',h.history_seq,'stepId',h.step_id,'attempt',h.attempt,
                'generation',h.generation,'kind',h.kind,'atMs',h.at_ms,'body',json(h.safe_body_json)) item
              FROM engine_workflow_history h
              WHERE h.run_id=$run AND h.machine=$machine AND h.history_seq>$after
              ORDER BY h.history_seq LIMIT $limit)
            SELECT CASE WHEN length(CAST(item AS BLOB))<=32768 THEN item END,length(CAST(item AS BLOB)),id,history_seq
            FROM candidates ORDER BY history_seq
            """, row => new Candidate(row.IsNull(0) ? null : row.Text(0), row.Long(1), row.Text(2), row.Long(3)),
            new SqlArg("$run", runId), new("$machine", machine), new("$after", cursor?.HistorySeq ?? 0), new("$limit", limit + 1));
        var selected = Take(rows, limit, 1024);
        var last = selected.LastOrDefault();
        var next = selected.Count < rows.Count && last is not null ? WorkflowReadModels.Cursor(
            new WorkflowHistoryReadCursor(machine, runId, last.Position), WorkflowReadJson.Default.WorkflowHistoryReadCursor) : null;
        return Page("items", selected, next);
    }

    private static void Limit(int limit)
    {
        if (limit is < 1 or > 200) throw new ArgumentException("bad_args");
    }

    private static void RequireRun(IReadScope scope, string runId, string machine)
    {
        if (scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_run r WHERE r.id=$run AND (r.machine=$machine OR EXISTS(
              SELECT 1 FROM engine_step st WHERE st.run_id=r.id AND st.authority_machine=$machine))
            """, row => row.Long(0), new SqlArg("$run", runId), new("$machine", machine)) != 1)
            throw new ArgumentException("workflow_run_not_found");
    }
}
