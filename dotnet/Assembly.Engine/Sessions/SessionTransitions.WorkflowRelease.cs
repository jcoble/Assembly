using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Workflows.Receipts;
using Assembly.Engine.Workflows.Reviews;

namespace Assembly.Engine.Sessions;

public static partial class SessionTransitions
{
    internal static CommandResult EndReleasedWorkflowWorker(IWriteScope scope, CommandContext context,
        WorkflowExecutionFence fence, MessageExpiryPolicy policy)
    {
        var current = Find(scope, context.EntityId);
        if (current is null || context.Actor.Kind != ActorKind.System || current.Machine != context.Machine ||
            current.Epoch != context.Epoch || current.Version != context.ExpectedVersion || current.ReservedEpoch is not null ||
            current.State is not ("idle" or "running" or "lost")) return Refused(context, "workflow_release_changed", current);
        if (!WorkflowExecutionReleases.IsReleased(scope, fence) || scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_step st JOIN engine_step_attempt attempt ON attempt.step_id=st.id
              AND attempt.attempt=st.attempt AND attempt.generation=st.generation
            JOIN engine_session worker ON worker.id=st.session_id
            WHERE st.id=$step AND st.attempt=$attempt AND st.generation=$generation AND st.state IN ('failed','cancelled')
              AND st.session_id=$session AND st.authority_session_id=$session AND st.authority_epoch=$epoch
              AND st.authority_machine=$machine AND attempt.session_id=$session AND attempt.execution_epoch=$epoch
              AND attempt.authority_machine=$machine AND worker.epoch=$epoch AND worker.machine=$machine
              AND worker.holder='local' AND worker.reserved_epoch IS NULL AND worker.run_id=st.run_id
            """, r => r.Long(0), new SqlArg("$step", fence.StepId), new("$attempt", fence.Attempt), new("$generation", fence.Generation),
            new("$session", context.EntityId), new("$epoch", fence.ExecutionEpoch), new("$machine", context.Machine)) != 1)
            return Refused(context, "workflow_release_changed", current);
        return SetState(scope, context, current, new("ended", null, "workflow_execution_released"), policy, checkedWorkflowRelease: true);
    }
}
