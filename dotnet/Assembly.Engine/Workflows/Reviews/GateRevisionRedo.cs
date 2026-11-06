using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Scheduling;

namespace Assembly.Engine.Workflows.Reviews;

internal sealed record GateRevision(string TargetStepId, string Text, string Actor);

public static partial class ReviewTransitions
{
    private const string GateRevisionSource = "gate";

    /// <summary>The owner's text becomes a finding like a reviewer's, one for each worker sent back. <c>c.EntityId</c> is the gate.</summary>
    internal static CommandResult GateRevised(IWriteScope s, CommandContext gate, string run, int attempt, string target, string text)
    {
        var c = gate with { CommandId = DispatchIdentity.Id("gate-revision-target/v1", gate.CommandId.ToString("N"), target) };
        var generation = checked((int)s.QueryFirst("SELECT generation FROM engine_step WHERE id=$target", r => r.Long(0), new SqlArg("$target", target)));
        History(s, c, run, c.EntityId, attempt, generation, "gate_revised",
            JsonSerializer.Serialize(new GateRevision(target, text, c.Actor.Id), GateRevisionJson.Default.GateRevision));
        return Request(s, c, run, attempt, generation, c.ExpectedVersion, target,
            c.CommandId.ToString("N") + "/gate_revised", [c.EntityId], GateRevisionSource);
    }

    /// <summary>The step the owner sent back always reads what was asked of it, in the prompt's own place if it has one.</summary>
    internal static string WithOwnerRevision(IReadScope s, string run, string step, int generation, string template, string prompt)
    {
        if (generation <= 0 || template.Contains("redo.findings", StringComparison.Ordinal)) return prompt;
        var text = s.QueryFirst("""
            WITH last AS (SELECT safe_body_json FROM engine_workflow_history WHERE run_id=$run AND kind='redo_applied'
              AND json_extract(safe_body_json,'$.targetStepId')=$step ORDER BY history_seq DESC LIMIT 1)
            SELECT json_extract(asked.safe_body_json,'$.text') FROM last
            JOIN json_each(last.safe_body_json,'$.findingReceiptIds') id
            JOIN engine_workflow_history asked ON asked.id=id.value AND asked.kind='gate_revised'
            """, r => r.Text(0), new SqlArg("$run", run), new("$step", step));
        return text is null ? prompt : prompt + "\n\nThe owner reviewed this at an approval step and asked for a revision:\n" + text;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GateRevision))]
internal sealed partial class GateRevisionJson : JsonSerializerContext;
