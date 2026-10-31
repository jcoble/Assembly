using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Engine.Recording;

public sealed class UsageQueries(IAppDatabase db)
{
    public UsageTotals Sum(string entityKind, string entityId, CancellationToken cancellationToken)
    {
        var column = entityKind switch
        {
            "session" => "session_id",
            "run" => "run_id",
            "step" => "step_id",
            _ => throw new ArgumentException("Expected session, run or step.", nameof(entityKind))
        };
        // SQLite SUM of INTEGERs raises on overflow; TOTAL would silently convert to floating point.
        var usage = db.QueryFirst($"""
            SELECT SUM(input_tokens),SUM(output_tokens),SUM(cached_tokens),SUM(cost_nanodollars),SUM(duration_ms)
            FROM engine_usage WHERE {column}=$id
            """, r => new UsageTotals(Number(r, 0), Number(r, 1), Number(r, 2), Number(r, 3), null, Number(r, 4), 0),
            cancellationToken, new SqlArg("$id", entityId))!;
        var tools = db.QueryFirst($"""
            SELECT SUM(ended_ms-started_ms),COUNT(*) FROM engine_tool_call WHERE {column}=$id
            """, r => (Duration: Number(r, 0), Count: r.Long(1)), cancellationToken, new SqlArg("$id", entityId));
        return usage with { ToolDurationMs = tools.Duration, ToolCalls = tools.Count };
    }

    private static long? Number(IRow row, int column) => row.IsNull(column) ? null : row.Long(column);
}
