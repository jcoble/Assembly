using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Workflows.Execution;

namespace Assembly.Engine.Workflows.Reviews;

internal sealed record WorkflowRedoValues(string TargetStepId, int Generation, IReadOnlyList<string> FindingReceiptIds,
    string? OutputReceiptId, string SourceDigest, JsonElement Values);
internal sealed record WorkflowRedoTemplate(IReadOnlyList<string> Findings, string? Output);

public static partial class ReviewTransitions
{
    /// <summary>Projects the consumed immutable redo, including its originating target for downstream steps.</summary>
    internal static WorkflowRedoValues? RedoValues(IReadScope s, string runId, string stepId, int generation)
    {
        if (generation <= 0) return null;
        var json = s.QueryFirst("""
            WITH RECURSIVE ancestors(id) AS (
              SELECT $step UNION SELECT d.dependency_id FROM engine_step_dependency d JOIN ancestors a ON d.step_id=a.id
            ) SELECT h.safe_body_json FROM engine_workflow_history h
            WHERE h.run_id=$run AND h.kind='redo_applied' AND h.generation=$previous
              AND json_extract(h.safe_body_json,'$.targetStepId') IN ancestors
            ORDER BY h.history_seq DESC LIMIT 1
            """, r => r.Text(0), new SqlArg("$run", runId), new("$step", stepId), new("$previous", generation - 1));
        if (json is null) return null;
        var applied = JsonSerializer.Deserialize(json, ReviewTransitionJson.Default.RedoApplied)!;
        var findings = Findings(s, applied.FindingReceiptIds);
        string? output = null;
        if (applied.OutputReceiptId is { } outputReceipt)
        {
            var outputs = s.Query("""
                SELECT h.safe_body_json FROM engine_workflow_history root JOIN engine_workflow_history h
                  ON h.run_id=root.run_id AND h.step_id=root.step_id AND h.generation=root.generation AND h.attempt=root.attempt
                WHERE root.id=$receipt AND root.run_id=$run AND root.kind='command_ended' AND h.kind='command_ended'
                ORDER BY h.history_seq
                """, r => r.Text(0), new SqlArg("$receipt", outputReceipt), new("$run", runId));
            if (outputs.Count == 0) throw new InvalidOperationException("redo_output_missing");
            output = string.Join("\n", outputs.Select(body =>
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.GetProperty("summary").GetString();
            }));
        }
        var values = JsonSerializer.SerializeToElement(new WorkflowRedoTemplate(findings, output), WorkflowRedoValueJson.Default.WorkflowRedoTemplate);
        // The original accepted receipt hashes/complete bodies remain durable; this digest freezes their consumed projection.
        var sources = s.Query("""
            SELECT r.id||':'||r.hash FROM engine_workflow_receipt r
            WHERE r.run_id=$run AND r.state='accepted' AND r.id IN (SELECT value FROM json_each($ids)) ORDER BY r.id
            """, r => r.Text(0), new SqlArg("$run", runId), new("$ids", JsonSerializer.Serialize(applied.FindingReceiptIds, EngineJson.Default.IReadOnlyListString)));
        var digest = CommandStepTransitions.Digest("workflow-redo-values/v1\n" + json + "\n" + string.Join("\n", sources) + "\n" + values.GetRawText());
        return new(applied.TargetStepId, generation, applied.FindingReceiptIds, applied.OutputReceiptId, digest, values);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowRedoTemplate))]
internal sealed partial class WorkflowRedoValueJson : JsonSerializerContext;
