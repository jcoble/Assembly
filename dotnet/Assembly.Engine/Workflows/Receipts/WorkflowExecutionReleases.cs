using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Workflows.Reviews;

namespace Assembly.Engine.Workflows.Receipts;

/// <summary>Reads positive owning-host release facts; terminal state or idle alone is never proof.</summary>
public static class WorkflowExecutionReleases
{
    // Shared SQL consumers bind the immutable engine_step_attempt row with the alias `attempt`.
    internal const string ExactAttemptPredicate = """
        EXISTS(SELECT 1 FROM engine_workflow_history released
          WHERE released.run_id=(SELECT run_id FROM engine_step WHERE id=attempt.step_id)
            AND released.step_id=attempt.step_id AND released.attempt=attempt.attempt AND released.generation=attempt.generation
            AND released.machine=attempt.authority_machine AND released.kind='execution_released'
            AND json_extract(released.safe_body_json,'$.stepId')=attempt.step_id
            AND json_extract(released.safe_body_json,'$.attempt')=attempt.attempt
            AND json_extract(released.safe_body_json,'$.generation')=attempt.generation
            AND json_extract(released.safe_body_json,'$.sessionId')=attempt.session_id
            AND json_extract(released.safe_body_json,'$.epoch')=attempt.execution_epoch
            AND json_extract(released.safe_body_json,'$.authorityMachine')=attempt.authority_machine
            AND json_extract(released.safe_body_json,'$.terminalSeq')>0
            AND length(json_extract(released.safe_body_json,'$.chatRunId'))>0
            AND json_extract(released.safe_body_json,'$.terminalState') IN ('finished','failed','cancelled')
            AND NOT EXISTS(SELECT 1 FROM rows newer WHERE newer.session_id=attempt.session_id AND newer.kind='turn'
              AND json_extract(newer.payload,'$.state')='started' AND newer.seq>json_extract(released.safe_body_json,'$.terminalSeq')))
        """;

    public static bool IsReleased(IReadScope scope, WorkflowExecutionFence fence) => scope.QueryFirst($"""
        SELECT EXISTS(SELECT 1 FROM engine_step_attempt attempt WHERE attempt.step_id=$step AND attempt.attempt=$attempt
          AND attempt.generation=$generation AND attempt.execution_epoch=$epoch AND {ExactAttemptPredicate})
        """, r => r.Long(0) != 0, new SqlArg("$step", fence.StepId), new("$attempt", fence.Attempt),
        new("$generation", fence.Generation), new("$epoch", fence.ExecutionEpoch));

    public static IReadOnlySet<WorkflowExecutionFence> Released(IReadScope scope, IReadOnlyList<WorkflowExecutionFence> fences)
    {
        var json = JsonSerializer.Serialize(fences, WorkflowReleaseJson.Default.IReadOnlyListWorkflowExecutionFence);
        return scope.Query($"""
            SELECT DISTINCT attempt.step_id,attempt.generation,attempt.attempt,attempt.execution_epoch
            FROM engine_step_attempt attempt JOIN json_each($fences) requested
              ON attempt.step_id=json_extract(requested.value,'$.stepId') AND attempt.attempt=json_extract(requested.value,'$.attempt')
                AND attempt.generation=json_extract(requested.value,'$.generation') AND attempt.execution_epoch=json_extract(requested.value,'$.executionEpoch')
            WHERE {ExactAttemptPredicate}
            """, r => new WorkflowExecutionFence(r.Text(0), (int)r.Long(1), (int)r.Long(2), r.Long(3)), new SqlArg("$fences", json)).ToHashSet();
    }

}
