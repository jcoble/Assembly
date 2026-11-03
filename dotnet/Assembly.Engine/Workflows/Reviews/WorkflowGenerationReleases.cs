using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Workflows.Execution;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Engine.Workflows.Reviews;

internal static class WorkflowGenerationReleases
{
    internal static IReadOnlySet<WorkflowExecutionFence> Released(IReadScope scope, IReadOnlyList<WorkflowExecutionFence> fences)
    {
        var facts = scope.Query($"""
            SELECT st.id,st.generation,a.attempt,a.execution_epoch,st.kind,
              st.kind='command' AND
                (EXISTS(SELECT 1 FROM engine_outbox original WHERE original.kind='workflow_command' AND original.run_id=st.run_id
                  AND json_extract(original.payload_json,'$.stepId')=st.id AND json_extract(original.payload_json,'$.generation')=st.generation
                  AND json_extract(original.payload_json,'$.attempt')=a.attempt AND json_extract(original.payload_json,'$.executionEpoch')=a.execution_epoch)
                 OR EXISTS(SELECT 1 FROM engine_workflow_history h WHERE h.step_id=st.id AND h.attempt=a.attempt
                   AND h.generation=st.generation AND h.kind='command_denied'))
                AND NOT EXISTS(SELECT 1 FROM engine_outbox o WHERE o.kind='workflow_command' AND o.run_id=st.run_id
                  AND json_extract(o.payload_json,'$.stepId')=st.id AND json_extract(o.payload_json,'$.attempt')=a.attempt
                  AND {CommandStepTransitions.ExecutorNotDrained})
            FROM json_each($fences) f JOIN engine_step st ON st.id=json_extract(f.value,'$.stepId')
            JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=json_extract(f.value,'$.attempt')
            WHERE st.generation=json_extract(f.value,'$.generation') AND a.generation=st.generation
              AND a.execution_epoch=json_extract(f.value,'$.executionEpoch')
            """, r => (Fence: new WorkflowExecutionFence(r.Text(0), checked((int)r.Long(1)), checked((int)r.Long(2)), r.Long(3)),
                Kind: r.Text(4), Drained: r.Long(5) != 0),
            new SqlArg("$fences", JsonSerializer.Serialize(fences, ReviewTransitionJson.Default.IReadOnlyListWorkflowExecutionFence)));
        var providers = facts.Where(row => row.Kind is "agent" or "review").Select(row => row.Fence).ToArray();
        var released = WorkflowExecutionReleases.Released(scope, providers).ToHashSet();
        foreach (var command in facts.Where(row => row.Kind == "command" && row.Drained)) released.Add(command.Fence);
        // Started gates require their Task10 checked decision fact; a null session is not release evidence.
        return released;
    }
}
