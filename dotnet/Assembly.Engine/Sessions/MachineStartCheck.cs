using Assembly.Application.Common.Database;

namespace Assembly.Engine.Sessions;

public static class MachineStartCheck
{
    public static string? Check(IWriteScope scope, string parentId, int depthCap, int childLimit)
    {
        var machine = scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0));
        if (machine is null) return "bad_args";
        var ancestry = SessionAncestry.Read(scope, machine, parentId);
        if (!ancestry.Known) return "bad_args";
        if (ancestry.Nodes.Count > Math.Max(1, depthCap)) return "depth_limit";
        var parent = ancestry.Nodes[0];
        if (parent.Machine != machine || parent.State == "foreign" || parent.Holder != "local") return "forbidden";
        if (parent.State is "ended" or "lost") return "invalid_transition";
        var children = scope.QueryFirst("""
            SELECT COUNT(*) FROM (
                SELECT id FROM engine_session WHERE parent_id=$parent AND state<>'ended' AND state<>'foreign'
                UNION
                SELECT r.entity_id FROM engine_mirror_row r JOIN engine_mirror m ON m.machine=r.machine
                    AND m.generation=r.generation AND m.state='live'
                WHERE r.entity_kind='session' AND json_extract(r.row_json,'$.parentId')=$parent
                    AND json_extract(r.row_json,'$.state') NOT IN ('ended','foreign')
            )
            """, r => r.Long(0), new SqlArg("$parent", parentId));
        return children >= Math.Clamp(childLimit, 1, 10) ? "child_limit" : null;
    }
}
