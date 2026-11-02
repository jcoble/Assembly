using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Execution;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Engine.Workflows.Reviews;

public static partial class ReviewTransitions
{
    internal static bool HasCommandTarget(IReadScope s, string step) => CommandTarget(s, step) is not null;

    /// <summary>The separate old on_fail trigger runs only after all real command receipts settle.</summary>
    internal static CommandResult CommandFailed(IWriteScope s, CommandContext c, CommandExecutionIntent intent, string receiptId)
    {
        var target = CommandTarget(s, intent.StepId) ?? throw new InvalidOperationException("command_redo_target_missing");
        if (c.Actor.Kind != ActorKind.System || c.EntityId != intent.StepId || s.QueryFirst("""
            SELECT COUNT(*) FROM engine_workflow_history WHERE id=$receipt AND run_id=$run AND step_id=$step
              AND attempt=$attempt AND generation=$generation AND kind='command_ended'
            """, r => r.Long(0), new SqlArg("$receipt", receiptId), new("$run", intent.RunId), new("$step", intent.StepId),
            new("$attempt", intent.Attempt), new("$generation", intent.Generation)) != 1)
            return new(false, false, "stale_attempt", c.ExpectedVersion, 0, c.Epoch);
        return Request(s, c, intent.RunId, intent.Attempt, intent.Generation, c.ExpectedVersion,
            target, receiptId, [intent.StepId], "command", receiptId);
    }

    private static string? CommandTarget(IReadScope s, string step)
    {
        var row = s.QueryFirst("""
            SELECT st.run_id,st.definition_key,snap.snapshot_json FROM engine_step st
            JOIN engine_workflow_snapshot snap ON snap.run_id=st.run_id WHERE st.id=$step AND st.kind='command'
            """, r => (Run: r.Text(0), Key: r.Text(1), Snapshot: r.Text(2)), new SqlArg("$step", step));
        if (row.Snapshot is null) return null;
        var body = JsonSerializer.Deserialize(row.Snapshot, WorkflowPreparationJson.Default.WorkflowStartBody)!;
        var target = body.Snapshot.Definition.Steps[row.Key].OnFail;
        return target is null ? null : body.StepIds[target];
    }
}
