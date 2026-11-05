using System.Text.Json;
using Assembly.Engine.Workflows.Runs;
using System.Text.Json.Serialization;

namespace Assembly.Engine.Workflows.Reading;

internal sealed record WorkflowRunReadCursor(string Machine, string? Project, string? State, long CreatedMs, string Id);
internal sealed record WorkflowStepReadCursor(string RunId, string DefinitionHash, int Ordinal, string Id);
internal sealed record WorkflowHistoryReadCursor(string Machine, string RunId, long HistorySeq);
internal sealed record WorkflowReceiptReadCursor(string Machine, string ReceiptId, string Hash, string Section,
    string Path, long NodeId, long Offset, bool Complete);
internal sealed record WorkflowStepOrder(string Id, string Key, int Ordinal);

/// <summary>One display projection shared by current-state streams and explicit workflow reads.</summary>
internal static class WorkflowReadModels
{
    internal const string RunColumns = "title,request,project_id,project_root,definition_path,definition_hash,stage,started_ms,ended_ms,paused,rerun_of,work_head,setup_state,cleanup_state,cleanup_reason";
    internal const string StepColumns = "definition_key,kind,result,generation,round,attempt_in_generation,workspace_id,authority_machine,authority_session_id,authority_epoch,detail,explanation,progress,required_scope,scope_granted,closed,receipt_id,dispatch_state,deadline_ms";

    internal static string Extend(string kind, string json) => kind switch
    {
        "run" => json[..^1] + "," + """
            'activeStepIds',json((SELECT json_group_array(id) FROM (
              SELECT id FROM engine_step WHERE run_id=t.id AND state IN ('running','waiting') ORDER BY id))),
            'reference',json(CASE WHEN t.machine=(SELECT machine_id FROM engine_meta WHERE singleton=1) THEN 'false' ELSE 'true' END),
            'ownerEpoch',(SELECT epoch FROM engine_session WHERE id=t.owner_session_id),
            'displayPrerequisite',CASE WHEN t.definition_hash IS NOT NULL AND NOT EXISTS(
              SELECT 1 FROM engine_workflow_snapshot snap WHERE snap.run_id=t.id AND snap.definition_hash=t.definition_hash)
              THEN 'workflow_snapshot_required' END)
            """,
        "step" => json[..^1] + "," + """
            'labelPresent',COALESCE(t.label_present,(SELECT json_extract(spec.value,'$.display.present')
              FROM engine_workflow_snapshot snap,json_each(snap.snapshot_json,'$.snapshot.definition.steps') spec
              WHERE snap.run_id=t.run_id AND spec.key=t.definition_key),
              (SELECT json_extract(spec.value,'$.display.label') FROM engine_workflow_snapshot snap,
                json_each(snap.snapshot_json,'$.snapshot.definition.steps') spec WHERE snap.run_id=t.run_id AND spec.key=t.definition_key),t.name),
            'labelPast',COALESCE(t.label_past,(SELECT json_extract(spec.value,'$.display.past')
              FROM engine_workflow_snapshot snap,json_each(snap.snapshot_json,'$.snapshot.definition.steps') spec
              WHERE snap.run_id=t.run_id AND spec.key=t.definition_key),
              (SELECT json_extract(spec.value,'$.display.label') FROM engine_workflow_snapshot snap,
                json_each(snap.snapshot_json,'$.snapshot.definition.steps') spec WHERE snap.run_id=t.run_id AND spec.key=t.definition_key),t.name),
            'stage',COALESCE((SELECT json_extract(spec.value,'$.display.stage') FROM engine_workflow_snapshot snap,
              json_each(snap.snapshot_json,'$.snapshot.definition.steps') spec WHERE snap.run_id=t.run_id AND spec.key=t.definition_key),
              CASE t.kind WHEN 'agent' THEN t.result WHEN 'command' THEN 'check' WHEN 'review' THEN 'review' WHEN 'gate' THEN 'needs_you' END),
            'badges',json(t.badges_json),
            """ + WorkflowStepRecovery.Field + ",\n" + """
            'needsMeIds',json((SELECT json_group_array(id) FROM (
              SELECT id FROM engine_needs_me WHERE step_id=t.id AND run_id=t.run_id AND state='open' ORDER BY id))),
            'dependencies',json((SELECT json_group_array(json_object('id',dependency_id,'optional',json(CASE optional WHEN 1 THEN 'true' ELSE 'false' END)))
              FROM (SELECT d.dependency_id,d.optional FROM engine_step_dependency d
                JOIN engine_step dependency ON dependency.id=d.dependency_id
                LEFT JOIN engine_workflow_snapshot snap ON snap.run_id=t.run_id
                LEFT JOIN json_each(snap.snapshot_json,'$.snapshot.definition.steps') spec ON spec.key=t.definition_key
                LEFT JOIN json_each(spec.value,'$.needs') declared ON declared.value=dependency.definition_key
                WHERE d.step_id=t.id ORDER BY CAST(declared.key AS INTEGER),d.dependency_id))))
            """,
        _ => json
    };

    internal static JsonElement Json(Action<Utf8JsonWriter> write)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) write(writer);
        if (bytes.Length > 1024 * 1024) throw new InvalidOperationException("workflow_page_too_large");
        using var document = JsonDocument.Parse(bytes.ToArray());
        return document.RootElement.Clone();
    }

    internal static string Cursor<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, type));

    internal static T ReadCursor<T>(string value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try { return JsonSerializer.Deserialize(Convert.FromBase64String(value), type) ?? throw new ArgumentException("invalid_cursor"); }
        catch (Exception error) when (error is FormatException or JsonException) { throw new ArgumentException("invalid_cursor", error); }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowRunReadCursor))]
[JsonSerializable(typeof(WorkflowStepReadCursor))]
[JsonSerializable(typeof(WorkflowHistoryReadCursor))]
[JsonSerializable(typeof(WorkflowReceiptReadCursor))]
[JsonSerializable(typeof(IReadOnlyList<WorkflowStepOrder>))]
internal sealed partial class WorkflowReadJson : JsonSerializerContext;
