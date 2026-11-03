using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows.Reviews;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    /// <summary>Pure readiness refuses without a receipt; the accepted original transition alone is journaled.</summary>
    public CommandResult AdvanceWorkflowGeneration(IWriteScope scope, CommandContext context)
    {
        const string canonical = "\"workflow_generation_advance/v1\"";
        CommandResult Refuse(string error) => new(false, false, error, context.ExpectedVersion, 0, context.Epoch);
        if (context.Machine != _machine) return Refuse("wrong_machine");
        if (context.Actor.Kind != ActorKind.System) return Refuse("forbidden");
        if (scope.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_command_receipt WHERE command_id=$id)", r => r.Long(0) != 0,
            new SqlArg("$id", context.CommandId.ToString("N"))))
            return EngineJournal.Execute(scope, context, canonical, () => throw new InvalidOperationException("workflow_advance_receipt_missing"));
        IReadOnlySet<WorkflowExecutionFence>? released = null;
        bool IsReleased(IReadScope read, WorkflowExecutionFence fence)
        {
            released ??= WorkflowGenerationReleases.Released(read, read.Query("""
                WITH RECURSIVE affected(id) AS (SELECT $target UNION SELECT d.step_id FROM engine_step_dependency d JOIN affected a ON d.dependency_id=a.id)
                SELECT st.id,st.generation,st.attempt,a.execution_epoch FROM engine_step st
                JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt
                WHERE st.id IN affected AND st.attempt>0
                """, r => new WorkflowExecutionFence(r.Text(0), checked((int)r.Long(1)), checked((int)r.Long(2)), r.Long(3)), new SqlArg("$target", context.EntityId)));
            return released.Contains(fence);
        }
        var prepared = ReviewTransitions.PrepareAdvance(scope, context, IsReleased, out var error);
        return prepared is null ? Refuse(error!) : EngineJournal.Execute(scope, context, canonical,
            () => ReviewTransitions.ApplyAdvance(scope, context, prepared));
    }
}
