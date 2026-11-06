using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Engine.Workflows.Runs;

internal sealed record OpenApproval(string Need, long Version, string Step, int Attempt, int Generation, string Kind);

/// <summary>Who a revision at an approval gate goes to: the nearest earlier steps that did the work being approved.</summary>
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

    /// <summary>The step ids a revision of this gate redoes, empty when nothing before it can act on one.</summary>
    internal static IReadOnlyList<string> Targets(IReadScope s, string gateStep)
    {
        var row = s.QueryFirst("""
            SELECT st.run_id,st.definition_key,snap.snapshot_json FROM engine_step st
            JOIN engine_workflow_snapshot snap ON snap.run_id=st.run_id WHERE st.id=$step
            """, r => (Run: r.Text(0), Key: r.IsNull(1) ? null : r.Text(1), Snapshot: r.Text(2)), new SqlArg("$step", gateStep));
        if (row.Snapshot is null || row.Key is null) return [];
        var steps = JsonSerializer.Deserialize(row.Snapshot, WorkflowPreparationJson.Default.WorkflowStartBody)?.Snapshot?.Definition?.Steps;
        if (steps is null) return [];
        var live = s.Query("SELECT definition_key,id FROM engine_step WHERE run_id=$run AND definition_key IS NOT NULL AND state<>'skipped'",
            r => (Key: r.Text(0), Id: r.Text(1)), new SqlArg("$run", row.Run)).ToDictionary(step => step.Key, step => step.Id);
        return Workers(steps, row.Key, live.ContainsKey).Select(key => live[key]).ToArray();
    }

    /// <summary>Redoing a target takes everything after it, so an approval still open there no longer asks about the
    /// work it would be approving. These are closed with the revision, and the rerun asks them again.</summary>
    internal static IReadOnlyList<OpenApproval> OpenApprovalsAfter(IReadScope s, string gateStep, string target) => s.Query("""
        WITH RECURSIVE affected(id) AS (
            SELECT $target UNION SELECT d.step_id FROM engine_step_dependency d JOIN affected a ON d.dependency_id=a.id
        ) SELECT n.id,n.version,st.id,st.attempt,st.generation,st.kind FROM engine_step st
          JOIN engine_needs_me n ON n.step_id=st.id AND n.run_id=st.run_id AND n.state='open'
          WHERE st.id IN affected AND st.id<>$gate AND st.kind IN ('gate','command') ORDER BY n.id
        """, r => new OpenApproval(r.Text(0), r.Long(1), r.Text(2), checked((int)r.Long(3)), checked((int)r.Long(4)), r.Text(5)),
        new SqlArg("$target", target), new("$gate", gateStep));

    /// <summary>Walks back along every path into the gate to the first step that did work; a review or a failed check's own
    /// target stands in for it, since those judge the work rather than do it. A worker that another found worker builds on
    /// is left out: that one is nearer, and the same as on a single path.</summary>
    internal static IReadOnlyList<string> Workers(IReadOnlyDictionary<string, WorkflowStepSpec> steps, string gate, Func<string, bool> usable)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var path = new Stack<string>(Earlier(steps, gate));
        while (path.TryPop(out var key))
        {
            if (!seen.Add(key) || !steps.TryGetValue(key, out var spec)) continue;
            var worker = spec.Kind switch { "agent" => key, "review" => spec.RedoTarget, "command" => spec.OnFail, _ => null };
            if (worker is not null && usable(worker)) found.Add(worker);
            else foreach (var earlier in Earlier(steps, key)) path.Push(earlier);
        }
        return found.Where(worker => !found.Any(other => other != worker && BuildsOn(steps, other, worker))).ToArray();
    }

    private static bool BuildsOn(IReadOnlyDictionary<string, WorkflowStepSpec> steps, string later, string earlier)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var path = new Stack<string>(Earlier(steps, later));
        while (path.TryPop(out var key))
        {
            if (key == earlier) return true;
            if (seen.Add(key)) foreach (var next in Earlier(steps, key)) path.Push(next);
        }
        return false;
    }

    private static IEnumerable<string> Earlier(IReadOnlyDictionary<string, WorkflowStepSpec> steps, string key) =>
        steps.TryGetValue(key, out var spec) && !spec.Needs.IsDefaultOrEmpty ? spec.Needs.Reverse() : [];
}
