using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Engine.Workflows.Runs;

/// <summary>Who a revision at an approval gate goes to: the nearest earlier step that did the work being approved.</summary>
internal static class WorkflowGateRevision
{
    /// <summary>The gate's approve or deny as a plain word, or a revision as <c>{"choice":"revise","text":"…"}</c>.</summary>
    internal static bool TryReadAnswer(JsonElement answer, out string? decision, out string? reply)
    {
        (decision, reply) = (null, null);
        if (answer.ValueKind == JsonValueKind.String)
        {
            decision = answer.GetString();
            return true;
        }
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("choice", out var choice) || choice.GetString() != "revise" ||
            !answer.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return false;
        (decision, reply) = ("revise", text.GetString());
        return true;
    }

    /// <summary>The step id a revision of this gate redoes, or null when nothing before it can act on one.</summary>
    internal static string? Target(IReadScope s, string gateStep)
    {
        var row = s.QueryFirst("""
            SELECT st.run_id,st.definition_key,snap.snapshot_json FROM engine_step st
            JOIN engine_workflow_snapshot snap ON snap.run_id=st.run_id WHERE st.id=$step
            """, r => (Run: r.Text(0), Key: r.IsNull(1) ? null : r.Text(1), Snapshot: r.Text(2)), new SqlArg("$step", gateStep));
        if (row.Snapshot is null || row.Key is null) return null;
        var steps = JsonSerializer.Deserialize(row.Snapshot, WorkflowPreparationJson.Default.WorkflowStartBody)?.Snapshot?.Definition?.Steps;
        if (steps is null) return null;
        var live = s.Query("SELECT definition_key,id FROM engine_step WHERE run_id=$run AND definition_key IS NOT NULL AND state<>'skipped'",
            r => (Key: r.Text(0), Id: r.Text(1)), new SqlArg("$run", row.Run)).ToDictionary(step => step.Key, step => step.Id);
        return Nearest(steps, row.Key, live.ContainsKey) is { } key ? live[key] : null;
    }

    /// <summary>Redoing the target takes everything after it, and an approval still open there can only end with an answer,
    /// so the redo would wait on it with this gate already answered. The owner answers that one first.</summary>
    internal static bool AnotherOpenApproval(IReadScope s, string gateStep, string target) => s.QueryFirst("""
        WITH RECURSIVE affected(id) AS (
            SELECT $target UNION SELECT d.step_id FROM engine_step_dependency d JOIN affected a ON d.dependency_id=a.id
        ) SELECT EXISTS(SELECT 1 FROM engine_step st JOIN engine_needs_me n ON n.step_id=st.id AND n.run_id=st.run_id AND n.state='open'
            WHERE st.id IN affected AND st.id<>$gate AND st.kind IN ('gate','command'))
        """, r => r.Long(0) != 0, new SqlArg("$target", target), new("$gate", gateStep));

    /// <summary>Walks back level by level. Of two steps at one level the later-declared wins, and a review or a failed
    /// check's own target stands in for it, since those judge the work rather than do it.</summary>
    internal static string? Nearest(IReadOnlyDictionary<string, WorkflowStepSpec> steps, string gate, Func<string, bool> usable)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<string> level = Earlier(steps, gate);
        while (level.Any())
        {
            List<string> next = [];
            foreach (var key in level)
            {
                if (!seen.Add(key) || !steps.TryGetValue(key, out var spec)) continue;
                var worker = spec.Kind switch { "agent" => key, "review" => spec.RedoTarget, "command" => spec.OnFail, _ => null };
                if (worker is not null && usable(worker)) return worker;
                next.AddRange(Earlier(steps, key));
            }
            level = next;
        }
        return null;
    }

    private static IEnumerable<string> Earlier(IReadOnlyDictionary<string, WorkflowStepSpec> steps, string key) =>
        steps.TryGetValue(key, out var spec) && !spec.Needs.IsDefaultOrEmpty ? spec.Needs.Reverse() : [];
}
