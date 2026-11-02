namespace Assembly.Engine.Reading;

/// <summary>Current-generation mirror display rows, using the ordinary snapshot's lifetime and pager.</summary>
internal static class MirrorSnapshot
{
    internal static string Query(string topic, string[] kinds)
    {
        var allowed = kinds.Where(kind => DisplayQueries.Kinds("mirror").Contains(kind));
        var list = string.Join(",", allowed.Select(kind => $"'{kind}'"));
        if (list.Length == 0) list = "NULL";
        var key = topic switch
        {
            "runs" => "CASE WHEN r.entity_kind IN ('step','handoff') THEN json_extract(r.row_json,'$.runId') ELSE r.entity_id END",
            "sessions" => "CASE WHEN r.entity_kind='handoff' THEN json_extract(r.row_json,'$.sessionId') ELSE r.entity_id END",
            _ => "r.entity_id"
        };
        return $"""
            SELECT r.entity_kind AS kind,r.entity_id AS id,r.row_json AS item,NULL AS prefix,NULL AS total,NULL AS label_prefix,NULL AS label_total
            FROM engine_mirror_row r JOIN engine_mirror m ON m.machine=r.machine AND m.generation=r.generation
            WHERE r.machine=$machine AND m.state<>'retired' AND r.entity_kind IN ({list}) AND ($key IS NULL OR {key}=$key)
            """;
    }
}
