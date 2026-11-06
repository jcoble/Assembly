using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Workflows.Reading;

public static partial class WorkflowQueries
{
    private const int ReceiptChunkBytes = 2048;
    private sealed record ReceiptDescriptor(string? Json, long Bytes, string Hash, string Kind);
    private sealed record ReceiptSection(string? Json, long Bytes, string Section, string Path, long NodeId, long Offset, bool Complete);

    public static JsonElement Receipt(IReadScope scope, string receiptId, string? after, int limit)
    {
        Limit(limit);
        var machine = Machine(scope);
        var receipt = scope.QueryFirst("""
            SELECT CASE WHEN length(CAST(item AS BLOB))<=32768 THEN item END,length(CAST(item AS BLOB)),hash,kind
            FROM (SELECT r.hash,r.kind,json_object('id',r.id,'version',r.version,'machine',r.machine,
              'createdMs',r.created_ms,'updatedMs',r.updated_ms,'runId',r.run_id,'stepId',r.step_id,
              'attempt',r.attempt,'generation',r.generation,'sessionId',r.session_id,'epoch',r.epoch,
              'hash',r.hash,'kind',r.kind,'state',r.state,'observedHead',r.observed_head,
              'checkedMs',r.checked_ms,'error',r.error) item
              FROM engine_workflow_receipt r WHERE r.id=$id AND r.machine=$machine)
            """, row => new ReceiptDescriptor(row.IsNull(0) ? null : row.Text(0), row.Long(1), row.Text(2), row.Text(3)),
            new SqlArg("$id", receiptId), new("$machine", machine)) ?? throw new ArgumentException("workflow_receipt_not_found");
        if (receipt.Json is null) throw new InvalidOperationException("workflow_display_item_too_large");
        // The stored hash is the canonical typed report body hash, not a claim about private capture data.
        var fields = ReceiptFields(receipt.Kind);
        var cursor = after is null ? null : WorkflowReadModels.ReadCursor(after, WorkflowReadJson.Default.WorkflowReceiptReadCursor);
        var ordinal = cursor is null ? -1 : Array.IndexOf(fields, cursor.Section);
        if (cursor is not null)
        {
            if (cursor.Machine != machine || cursor.ReceiptId != receiptId || cursor.Hash != receipt.Hash || ordinal < 0 || cursor.Offset < 0)
                throw new ArgumentException("invalid_cursor");
            var anchor = scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_workflow_receipt r,json_tree(r.body_json,'$.report.receipt.'||$section) j
                WHERE r.id=$id AND r.machine=$machine AND j.id=$node AND j.fullkey=$path
                  AND (j.type='text' AND $offset<=length(CAST(j.atom AS BLOB)) OR j.type<>'text' AND $offset=0)
                """, row => row.Long(0), new SqlArg("$id", receiptId), new("$machine", machine), new("$section", cursor.Section),
                new("$node", cursor.NodeId), new("$path", cursor.Path), new("$offset", cursor.Offset));
            if (anchor != 1) throw new ArgumentException("invalid_cursor");
        }
        var names = JsonSerializer.Serialize((IReadOnlyList<string>)fields, EngineJson.Default.IReadOnlyListString);
        var start = Boundary("text_bytes", "nominal");
        var end = Boundary("text_bytes", $"MIN(nominal+{ReceiptChunkBytes},length(text_bytes))");
        var rows = scope.Query($"""
            WITH RECURSIVE indices(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM indices WHERE n<$limit),
            nodes AS MATERIALIZED (
              SELECT CAST(f.key AS INTEGER) section_ordinal,f.value section,j.id node_id,j.fullkey path,j.type,
                CASE WHEN j.type='text' THEN CAST(j.atom AS BLOB) END text_bytes,
                CASE j.type WHEN 'true' THEN 'true' WHEN 'false' THEN 'false'
                  WHEN 'integer' THEN CAST(j.atom AS TEXT) WHEN 'real' THEN CAST(j.atom AS TEXT) ELSE 'null' END scalar_json
              FROM engine_workflow_receipt r,json_each($fields) f,json_tree(r.body_json,'$.report.receipt.'||f.value) j
              WHERE r.id=$id AND r.machine=$machine AND (CAST(f.key AS INTEGER)>$section OR
                CAST(f.key AS INTEGER)=$section AND (j.id>$node OR j.id=$node AND $complete=0))
              ORDER BY section_ordinal,j.id LIMIT $limit+1),
            chunks AS (
              SELECT *,((CASE WHEN section_ordinal=$section AND node_id=$node THEN $offset/{ReceiptChunkBytes}+1 ELSE 0 END)+n)*{ReceiptChunkBytes} nominal
              FROM nodes CROSS JOIN indices WHERE type='text' OR n=0),
            bounded AS (
              SELECT *,{start} byte_start,{end} byte_end FROM chunks
              WHERE type<>'text' OR nominal<length(text_bytes) OR nominal=0 AND length(text_bytes)=0),
            items AS (
              SELECT *,CASE WHEN type='text' THEN json_object('section',section,'path',path,'type','string',
                  'value',CAST(substr(text_bytes,byte_start+1,byte_end-byte_start) AS TEXT),
                  'offset',byte_start,'complete',json(CASE WHEN byte_end=length(text_bytes) THEN 'true' ELSE 'false' END))
                ELSE json_object('section',section,'path',path,'type',CASE type WHEN 'integer' THEN 'number'
                  WHEN 'real' THEN 'number' WHEN 'true' THEN 'boolean' WHEN 'false' THEN 'boolean' ELSE type END,
                  'value',json(scalar_json)) END item FROM bounded)
            SELECT CASE WHEN length(CAST(item AS BLOB))<=32768 THEN item END,length(CAST(item AS BLOB)),
              CASE WHEN length(CAST(item AS BLOB))<=32768 THEN section END,
              CASE WHEN length(CAST(item AS BLOB))<=32768 THEN path END,node_id,
              COALESCE(byte_start,0),CASE WHEN type='text' AND byte_end<length(text_bytes) THEN 0 ELSE 1 END
            FROM items ORDER BY section_ordinal,node_id,nominal LIMIT $limit+1
            """, row => new ReceiptSection(row.IsNull(0) ? null : row.Text(0), row.Long(1),
                row.IsNull(2) ? "" : row.Text(2), row.IsNull(3) ? "" : row.Text(3), row.Long(4), row.Long(5), row.Long(6) != 0),
            new SqlArg("$id", receiptId), new("$machine", machine), new("$fields", names), new("$limit", limit),
            new("$section", ordinal), new("$node", cursor?.NodeId ?? -1), new("$offset", cursor?.Offset ?? 0), new("$complete", cursor?.Complete == false ? 0 : 1));
        // A continuation can carry a long actual JSON path; reserve its escaped/base64 envelope too.
        List<ReceiptSection> selected = []; var bytes = receipt.Bytes + 64 * 1024;
        foreach (var row in rows.Take(limit))
        {
            if (row.Json is null) throw new InvalidOperationException("workflow_display_item_too_large");
            if (bytes + row.Bytes + 1 > PageBudget) break;
            bytes += row.Bytes + 1; selected.Add(row);
        }
        var last = selected.LastOrDefault();
        var next = selected.Count < rows.Count && last is not null ? WorkflowReadModels.Cursor(
            new WorkflowReceiptReadCursor(machine, receiptId, receipt.Hash, last.Section, last.Path, last.NodeId, last.Offset, last.Complete),
            WorkflowReadJson.Default.WorkflowReceiptReadCursor) : null;
        return WorkflowReadModels.Json(writer =>
        {
            writer.WriteStartObject(); writer.WritePropertyName("receipt"); writer.WriteRawValue(receipt.Json);
            writer.WriteStartArray("sections"); foreach (var row in selected) writer.WriteRawValue(row.Json!); writer.WriteEndArray();
            if (next is not null) writer.WriteString("next", next); writer.WriteEndObject();
        });
    }

    // Shift a fixed BLOB byte boundary forward over at most three UTF-8 continuation bytes.
    private static string Boundary(string blob, string position) => $"""
        ({position})+CASE WHEN hex(substr({blob},({position})+1,1)) BETWEEN '80' AND 'BF' THEN
          CASE WHEN hex(substr({blob},({position})+2,1)) BETWEEN '80' AND 'BF' THEN
            CASE WHEN hex(substr({blob},({position})+3,1)) BETWEEN '80' AND 'BF' THEN 3 ELSE 2 END ELSE 1 END ELSE 0 END
        """;

    private static string[] ReceiptFields(string kind) => kind switch
    {
        "build" => ["commits", "tests_added", "summary"],
        "recon" => ["summary", "existing", "none_found_because", "touches"],
        "spec" => ["url", "summary", "requirements", "acceptance", "reuse", "budget", "assumptions", "path"],
        "plan" => ["url", "slices", "path"],
        "review" => ["verdict", "findings", "accepted", "new_names", "head"],
        "proof" => ["summary", "files", "proofUrls", "head", "checks"],
        _ => throw new InvalidOperationException("workflow_receipt_kind_unknown")
    };
}
