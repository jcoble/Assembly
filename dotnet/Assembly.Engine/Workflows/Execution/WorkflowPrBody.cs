using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Workflows.Execution;

public sealed record WorkflowPrBody(string Path, int Generation, string InputSetSha256, string Sha256, string Text);
public sealed record WorkflowPrBodySection(string Key, string Summary);
public sealed record WorkflowPrBodySource(string? TaskUrl, string Request, IReadOnlyList<WorkflowPrBodySection> Steps,
    WorkflowPrBodySection? LatestReview, IReadOnlyList<string> CheckedReferences);
internal sealed record PrBodyInput(string StepId, int Generation, int Attempt, string ReceiptId, string Hash, string? CheckId);

/// <summary>Only this derived file is clipped. Full source receipts and the request remain in the run.</summary>
public static class WorkflowPrBodies
{
    private const string Cut = "… (cut; the full output is in the run)";
    public static string Render(WorkflowPrBodySource source)
    {
        var output = new StringBuilder();
        if (source.TaskUrl is { } task) output.Append("Task: ").AppendLine(task);
        else output.Append("Request: ").AppendLine(Clip(source.Request, 4000));
        foreach (var step in source.Steps) output.AppendLine().AppendLine(Clip(step.Key + ": " + step.Summary, 4000));
        if (source.LatestReview is { } review)
            output.AppendLine().AppendLine("Latest checked review").AppendLine(Clip(review.Key + ": " + review.Summary, 4000));
        foreach (var reference in source.CheckedReferences) output.AppendLine().AppendLine(reference);
        return Clip(output.ToString(), 60000);
    }
    private static string Clip(string text, int limit)
    {
        var runes = text.EnumerateRunes().Take(limit + 1).ToArray();
        return runes.Length <= limit ? text : string.Concat(runes.Take(limit - Cut.EnumerateRunes().Count()).Select(rune => rune.ToString())) + Cut;
    }

    internal static WorkflowPrBody Prepare(IReadScope s, string run, string step, int generation)
    {
        var info = s.QueryFirst("SELECT task_url,request,pr_body FROM engine_run WHERE id=$run",
            r => (Task: r.IsNull(0) ? null : r.Text(0), Request: r.Text(1), Path: r.Text(2)), new SqlArg("$run", run));
        var materials = s.Query(MaterialCte + """
            SELECT st.definition_key,r.kind,COALESCE(json_extract(r.body_json,'$.report.receipt.summary'),json_extract(r.body_json,'$.report.summary'),''),h.safe_body_json,r.observed_head,r.checked_ms,r.id
            """ + MaterialFrom + " ORDER BY st.definition_key,r.id", r => new
        {
            Key = r.Text(0),
            Kind = r.Text(1),
            Body = r.Text(2),
            Metadata = r.IsNull(3) ? null : r.Text(3),
            Head = r.IsNull(4) ? null : r.Text(4),
            Checked = r.IsNull(5) ? 0 : r.Long(5),
            Id = r.Text(6)
        }, new SqlArg("$run", run));
        var references = new List<string>();
        foreach (var material in materials.Where(material => material.Metadata is not null))
        {
            using var document = JsonDocument.Parse(material.Metadata!);
            var metadata = document.RootElement;
            if (metadata.TryGetProperty("files", out var files))
                foreach (var file in files.EnumerateArray())
                    references.Add($"Checked file: {file.GetProperty("path").GetString()} (sha256 {file.GetProperty("sha256").GetString()}, {file.GetProperty("bytes").GetInt64()} bytes)");
            if (metadata.TryGetProperty("evidence", out var evidence))
                foreach (var item in evidence.EnumerateArray())
                    if (material.Head is not null && item.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String && result.GetString() == "reachable" &&
                        item.TryGetProperty("testedHead", out var testedHead) && testedHead.ValueKind == JsonValueKind.String && testedHead.GetString() == material.Head &&
                        item.TryGetProperty("url", out var link) && link.ValueKind == JsonValueKind.String && link.GetString() is { } url)
                        references.Add($"Access verified: {url} (tested head {material.Head})");
        }
        var latest = materials.Where(material => material.Kind == "review").OrderByDescending(material => material.Checked).ThenByDescending(material => material.Id, StringComparer.Ordinal).FirstOrDefault();
        var text = Render(new(info.Task, info.Request, materials.Select(material => new WorkflowPrBodySection(material.Key, material.Body)).ToArray(),
            latest is null ? null : new(latest.Key, latest.Body), references));
        return new(info.Path, generation, InputSetHash(s, run, step, generation), CommandStepTransitions.Digest(text), text);
    }

    internal static string InputSetHash(IReadScope s, string run, string step, int generation)
    {
        var inputs = s.Query(MaterialCte + "SELECT r.step_id,r.generation,r.attempt,r.id,r.hash,h.id" + MaterialFrom + " ORDER BY r.id,h.id",
            r => new PrBodyInput(r.Text(0), checked((int)r.Long(1)), checked((int)r.Long(2)), r.Text(3), r.Text(4), r.IsNull(5) ? null : r.Text(5)), new SqlArg("$run", run));
        return CommandStepTransitions.Digest("workflow-pr-body-inputs/v1\n" + run + "\n" + step + "\n" +
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + JsonSerializer.Serialize((IReadOnlyList<PrBodyInput>)inputs, WorkflowPrBodyJson.Default.IReadOnlyListPrBodyInput));
    }
    private const string MaterialCte = """
        WITH picked(id) AS (
          SELECT receipt.id FROM engine_step step JOIN engine_workflow_receipt receipt ON receipt.id=step.receipt_id
          WHERE step.run_id=$run AND receipt.state='accepted' AND receipt.generation=step.generation AND receipt.attempt=step.attempt
          UNION SELECT id FROM (SELECT id FROM engine_workflow_receipt WHERE run_id=$run AND kind='review' AND state='accepted'
            ORDER BY checked_ms DESC,created_ms DESC,id DESC LIMIT 1)
        )
        """;
    private const string MaterialFrom = """
         FROM engine_workflow_receipt r JOIN picked p ON p.id=r.id JOIN engine_step st ON st.id=r.step_id
         LEFT JOIN engine_workflow_history h ON h.run_id=r.run_id AND h.step_id=r.step_id AND h.attempt=r.attempt AND h.generation=r.generation
           AND h.kind='receipt_checked' AND json_extract(h.safe_body_json,'$.receiptId')=r.id AND json_extract(h.safe_body_json,'$.check.accepted')=1
        """;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowPrBody))]
[JsonSerializable(typeof(IReadOnlyList<PrBodyInput>))]
internal sealed partial class WorkflowPrBodyJson : JsonSerializerContext;
