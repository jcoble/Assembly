using Assembly.Application.Common.Database;

namespace Assembly.Engine.Sessions;

public static class MachineStartCheck
{
    public static string? Check(IWriteScope scope, string parentId, int depthCap, int childLimit) =>
        Check(scope, parentId, depthCap, childLimit, [], out _);

    public static string? Check(IWriteScope scope, string parentId, int depthCap, int childLimit,
        IReadOnlyList<MachineStartMirrorGeneration> eligibleOwners, out IReadOnlyList<MachineStartCensusCut> censusCuts)
    {
        censusCuts = [];
        var machine = scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0));
        if (machine is null) return "bad_args";
        var ancestry = SessionAncestry.Read(scope, machine, parentId);
        if (!ancestry.Known) return "bad_args";
        if (ancestry.Nodes.Count > Math.Max(1, depthCap)) return "depth_limit";
        var parent = ancestry.Nodes[0];
        if (parent.Machine != machine || parent.State == "foreign" || parent.Holder != "local") return "forbidden";
        if (parent.State is "ended" or "lost") return "invalid_transition";
        if (eligibleOwners.Any(owner => string.IsNullOrWhiteSpace(owner.Machine) || owner.Machine == machine || owner.Generation < 0) ||
            eligibleOwners.Select(owner => owner.Machine).Distinct(StringComparer.Ordinal).Count() != eligibleOwners.Count)
            return "bad_args";
        var eligibleJson = System.Text.Json.JsonSerializer.Serialize(eligibleOwners, EngineJson.Default.IReadOnlyListMachineStartMirrorGeneration);
        var census = scope.QueryFirst("""
            WITH owners AS (
                SELECT json_extract(value,'$.machine') AS machine,
                       json_extract(value,'$.generation') AS generation FROM json_each($eligibleOwners)
            ), cuts AS (
                SELECT m.machine,m.generation,m.through,m.at_json
                FROM owners o JOIN engine_mirror m
                  ON m.machine=o.machine AND m.generation=o.generation AND m.state='live'
            ), children AS (
                SELECT id FROM engine_session
                 WHERE machine=$sourceMachine AND state NOT IN ('foreign','ended') AND parent_id=$parent
                UNION
                SELECT r.entity_id FROM engine_mirror_row r JOIN cuts c
                  ON r.machine=c.machine AND r.generation=c.generation
                 WHERE r.entity_kind='session' AND json_extract(r.row_json,'$.machine')=r.machine
                   AND json_extract(r.row_json,'$.parentId')=$parent
                   AND json_extract(r.row_json,'$.state') IS NOT 'ended'
            )
            SELECT (SELECT COUNT(*) FROM cuts),(SELECT COUNT(*) FROM children),
                   (SELECT json_group_array(json_object('machine',machine,'generation',generation,
                       'through',through,'atJson',at_json)) FROM cuts)
            """, r => (Matched: r.Long(0), Children: r.Long(1), Cuts: r.Text(2)),
            new SqlArg("$parent", parentId), new SqlArg("$sourceMachine", machine), new SqlArg("$eligibleOwners", eligibleJson));
        if (census.Matched != eligibleOwners.Count) return "mirror_unavailable";
        using var cuts = System.Text.Json.JsonDocument.Parse(census.Cuts);
        censusCuts = cuts.RootElement.EnumerateArray().Select(cut => new MachineStartCensusCut(
            cut.GetProperty("machine").GetString()!, cut.GetProperty("generation").GetInt64(),
            cut.GetProperty("through").GetInt64(), cut.GetProperty("atJson").ValueKind == System.Text.Json.JsonValueKind.Null ? null :
                System.Text.Json.JsonSerializer.Deserialize(cut.GetProperty("atJson").GetString()!, EngineJson.Default.CursorAt))).ToArray();
        return census.Children >= Math.Clamp(childLimit, 1, 10) ? "child_limit" : null;
    }
}
