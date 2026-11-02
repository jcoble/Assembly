using Assembly.Application.Common.Database;
using Assembly.Engine.Hosting;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Mcp;

public sealed class McpGate(IEngineRuntime runtime, SessionLimits limits, TimeProvider time)
{
    public string Machine => runtime.Machine;

    public T Write<T>(McpCaller caller, Func<McpWrite, T> body) => runtime.Write(scope =>
        body(new(Require(scope, caller), scope, runtime, limits, time)));

    public T Read<T>(McpCaller caller, Func<CallerRow, IReadScope, T> body, CancellationToken cancellationToken) =>
        runtime.Read(scope => body(Require(scope, caller), scope), cancellationToken);

    private CallerRow Require(IReadScope scope, McpCaller caller) =>
        Resolve(scope, caller) ?? throw new McpRefused(new("session_gone"));

    public CallerRow? Resolve(IReadScope scope, McpCaller caller)
    {
        var row = scope.QueryFirst("""
            SELECT s.id,s.epoch,s.state,s.parent_id,0,
                COALESCE(st.run_id,s.run_id),st.id,st.attempt,s.provider,s.model,s.folder,CAST(s.title AS BLOB),s.managed_checkout
            FROM engine_session s
            LEFT JOIN engine_step st ON st.id=(
                SELECT id FROM engine_step WHERE session_id=s.id AND state IN ('running','waiting')
                ORDER BY updated_ms DESC,id LIMIT 1)
            WHERE s.id=$id AND s.machine=$machine AND s.holder='local' AND s.state<>'foreign'
            """, r => new CallerRow(r.Text(0), r.Long(1), r.Text(2), r.IsNull(3) ? null : r.Text(3),
                r.Long(4), r.IsNull(5) ? null : r.Text(5), r.IsNull(6) ? null : r.Text(6),
                r.IsNull(7) ? null : checked((int)r.Long(7)), r.Text(8), r.IsNull(9) ? null : r.Text(9), r.Text(10), new System.Text.UTF8Encoding(false, true).GetString(r.Blob(11)), r.Long(12) == 1),
            new("$id", caller.SessionId), new("$machine", runtime.Machine));
        if (row is null || row.State is "ended" or "lost") return null;
        if (row.Epoch != caller.Epoch) throw new McpRefused(new("stale_epoch"));
        if (row.State == "handing_off") throw new McpRefused(new("handing_off"));
        var ancestry = SessionAncestry.Read(scope, runtime.Machine, row.SessionId);
        if (!ancestry.Known) throw new McpRefused(new("bad_args"));
        return row with { Depth = ancestry.Nodes.Count - 1L };
    }
}
