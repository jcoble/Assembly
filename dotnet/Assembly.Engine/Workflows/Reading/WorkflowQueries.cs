using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Reading;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Engine.Workflows.Reading;

public static partial class WorkflowQueries
{
    public static JsonElement Settings(IReadScope scope) => scope.QueryFirst(
        "SELECT json_object('id','workflow-settings','version',version,'mutedUntil',muted_until) FROM engine_workflow_settings WHERE singleton=1",
        row => DisplayQueries.Parse(row.Text(0)));

    private const int ItemBudget = 32 * 1024;
    private const int PageBudget = 1024 * 1024;
    private sealed record Candidate(string? Json, long Bytes, string Id, long Position);

    public static JsonElement Runs(IReadScope scope, JsonElement filters) => Runs(scope, filters, Machine(scope));

    internal static JsonElement Runs(IReadScope scope, JsonElement filters, string machine)
    {
        if (filters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) throw new ArgumentException("bad_args");
        var project = Text(filters, "project"); var state = Text(filters, "state"); var after = Text(filters, "after");
        var limit = filters.ValueKind == JsonValueKind.Object && filters.TryGetProperty("limit", out var count) ? count.GetInt32() : 50;
        if (limit is < 1 or > 200) throw new ArgumentException("bad_args");
        var cursor = after is null ? null : WorkflowReadModels.ReadCursor(after, WorkflowReadJson.Default.WorkflowRunReadCursor);
        if (cursor is not null && (cursor.Machine != machine || cursor.Project != project || cursor.State != state)) throw new ArgumentException("invalid_cursor");
        var rows = scope.Query($"""
            WITH candidates AS (
              SELECT t.id,t.created_ms,{DisplayQueries.Json("run")} item FROM engine_run t
              WHERE t.machine=$machine AND t.definition_hash IS NOT NULL
                AND ($project IS NULL OR t.project_id=$project) AND ($state IS NULL OR t.state=$state)
                AND ($after=0 OR t.created_ms<$created OR t.created_ms=$created AND t.id<$id)
              ORDER BY t.created_ms DESC,t.id DESC LIMIT $limit)
            SELECT CASE WHEN length(CAST(item AS BLOB))<=32768 THEN item END,length(CAST(item AS BLOB)),id,created_ms
            FROM candidates ORDER BY created_ms DESC,id DESC
            """, row => new Candidate(row.IsNull(0) ? null : row.Text(0), row.Long(1), row.Text(2), row.Long(3)),
            new SqlArg("$machine", machine), new("$project", project), new("$state", state), new("$after", cursor is null ? 0 : 1),
            new("$created", cursor?.CreatedMs ?? 0), new("$id", cursor?.Id ?? ""), new("$limit", limit + 1));
        var selected = Take(rows, limit, 1024);
        var last = selected.LastOrDefault();
        var next = selected.Count < rows.Count && last is not null ? WorkflowReadModels.Cursor(
            new WorkflowRunReadCursor(machine, project, state, last.Position, last.Id), WorkflowReadJson.Default.WorkflowRunReadCursor) : null;
        return Page("items", selected, next);
    }

    public static JsonElement Run(IReadScope scope, string runId, string? after) => Run(scope, runId, after, Machine(scope), default);

    internal static JsonElement Run(IReadScope scope, string runId, string? after, string machine, CancellationToken ct)
    {
        var run = scope.QueryFirst($"""
            SELECT CASE WHEN length(CAST(item AS BLOB))<=32768 THEN item END,length(CAST(item AS BLOB)),id,0
            FROM (SELECT t.id,{DisplayQueries.Json("run")} item FROM engine_run t
              WHERE t.id=$run AND (t.machine=$machine OR EXISTS(
                SELECT 1 FROM engine_step st WHERE st.run_id=t.id AND st.authority_machine=$machine)))
            """, row => new Candidate(row.IsNull(0) ? null : row.Text(0), row.Long(1), row.Text(2), row.Long(3)),
            new SqlArg("$run", runId), new("$machine", machine)) ?? throw new ArgumentException("workflow_run_not_found");
        Check(run);
        var definition = Definition(scope, runId, ct);
        var cursor = after is null ? null : WorkflowReadModels.ReadCursor(after, WorkflowReadJson.Default.WorkflowStepReadCursor);
        if (cursor is not null && (cursor.RunId != runId || cursor.DefinitionHash != definition.Hash ||
            !definition.Order.Any(item => item.Ordinal == cursor.Ordinal && item.Id == cursor.Id))) throw new ArgumentException("invalid_cursor");
        var order = JsonSerializer.Serialize((IReadOnlyList<WorkflowStepOrder>)definition.Order, WorkflowReadJson.Default.IReadOnlyListWorkflowStepOrder);
        var rows = scope.Query($"""
            WITH candidates AS (
              SELECT t.id,CAST(json_extract(o.value,'$.ordinal') AS INTEGER) ordinal,{DisplayQueries.Json("step")} item
              FROM json_each($order) o JOIN engine_step t ON t.id=json_extract(o.value,'$.id')
              JOIN engine_run r ON r.id=t.run_id
              WHERE t.run_id=$run AND t.definition_key=json_extract(o.value,'$.key')
                AND (r.machine=$machine OR t.authority_machine=$machine)
                AND (CAST(json_extract(o.value,'$.ordinal') AS INTEGER)>$ordinal OR
                  CAST(json_extract(o.value,'$.ordinal') AS INTEGER)=$ordinal AND t.id>$id)
              ORDER BY ordinal,t.id LIMIT 201)
            SELECT CASE WHEN length(CAST(item AS BLOB))<=32768 THEN item END,length(CAST(item AS BLOB)),id,ordinal
            FROM candidates ORDER BY ordinal,id
            """, row => new Candidate(row.IsNull(0) ? null : row.Text(0), row.Long(1), row.Text(2), row.Long(3)),
            new SqlArg("$order", order), new("$run", runId), new("$machine", machine), new("$ordinal", cursor?.Ordinal ?? -1), new("$id", cursor?.Id ?? ""));
        var selected = Take(rows, 200, run.Bytes + 1024);
        var last = selected.LastOrDefault();
        var next = selected.Count < rows.Count && last is not null ? WorkflowReadModels.Cursor(
            new WorkflowStepReadCursor(runId, definition.Hash, checked((int)last.Position), last.Id), WorkflowReadJson.Default.WorkflowStepReadCursor) : null;
        return WorkflowReadModels.Json(writer =>
        {
            writer.WriteStartObject(); writer.WritePropertyName("run"); writer.WriteRawValue(run.Json!);
            Items(writer, "steps", selected); if (next is not null) writer.WriteString("next", next); writer.WriteEndObject();
        });
    }

    private static (string Hash, List<WorkflowStepOrder> Order) Definition(IReadScope scope, string run, CancellationToken ct)
    {
        var snapshot = scope.QueryFirst("""
            SELECT snapshot.definition_hash,json_extract(snapshot.snapshot_json,'$.snapshot.yaml'),
              json_extract(snapshot.snapshot_json,'$.stepIds') FROM engine_workflow_snapshot snapshot
            JOIN engine_run r ON r.id=snapshot.run_id AND r.definition_hash=snapshot.definition_hash
            WHERE snapshot.run_id=$run
            """, row => (Hash: row.Text(0), Yaml: row.IsNull(1) ? null : row.Text(1), Ids: row.IsNull(2) ? null : row.Text(2)), new SqlArg("$run", run));
        if (snapshot.Yaml is null || snapshot.Ids is null) throw new InvalidOperationException("workflow_snapshot_required");
        using var ids = JsonDocument.Parse(snapshot.Ids);
        var yaml = WorkflowYaml.Parse(snapshot.Yaml, ct);
        if (!yaml.TryGetProperty("steps", out var steps)) throw new InvalidOperationException("workflow_snapshot_invalid");
        var order = steps.EnumerateObject().Select((step, index) => new WorkflowStepOrder(
            ids.RootElement.GetProperty(step.Name).GetString() ?? throw new InvalidOperationException("workflow_snapshot_invalid"), step.Name, index)).ToList();
        if (order.Count != ids.RootElement.EnumerateObject().Count()) throw new InvalidOperationException("workflow_snapshot_invalid");
        return (snapshot.Hash, order);
    }

    private static List<Candidate> Take(List<Candidate> rows, int count, long bytes)
    {
        List<Candidate> selected = [];
        foreach (var row in rows.Take(count))
        {
            Check(row);
            if (bytes + row.Bytes + 1 > PageBudget) break;
            bytes += row.Bytes + 1; selected.Add(row);
        }
        return selected;
    }
    private static void Check(Candidate row)
    {
        if (row.Json is null || row.Bytes > ItemBudget) throw new InvalidOperationException("workflow_display_item_too_large");
    }
    private static string Machine(IReadScope scope) => scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0))
        ?? throw new InvalidOperationException("engine_machine_required");
    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item)
        && item.ValueKind != JsonValueKind.Null ? item.GetString() : null;
    private static void Items(Utf8JsonWriter writer, string name, List<Candidate> rows)
    {
        writer.WriteStartArray(name); foreach (var row in rows) writer.WriteRawValue(row.Json!); writer.WriteEndArray();
    }
    private static JsonElement Page(string name, List<Candidate> rows, string? next) => WorkflowReadModels.Json(writer =>
    {
        writer.WriteStartObject(); Items(writer, name, rows); if (next is not null) writer.WriteString("next", next); writer.WriteEndObject();
    });
}
