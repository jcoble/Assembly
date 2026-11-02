using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Workflows.Runs;

public static partial class WorkflowGateTransitions
{
    // Alias 'attempt' is the immutable engine_step_attempt being checked by a batch consumer.
    internal const string DecidedAttemptPredicate = """
        attempt.session_id IS NULL AND attempt.workspace_id IS NULL AND attempt.gate_state='settled' AND EXISTS(
          SELECT 1 FROM engine_workflow_history h JOIN engine_step gate ON gate.id=attempt.step_id
          JOIN engine_needs_me need ON need.id=json_extract(h.safe_body_json,'$.needId')
          WHERE gate.kind='gate' AND h.run_id=gate.run_id AND h.step_id=attempt.step_id
            AND h.attempt=attempt.attempt AND h.generation=attempt.generation AND h.machine=attempt.authority_machine
            AND h.kind='gate_decided' AND need.source_kind='gate' AND need.run_id=gate.run_id AND need.step_id=gate.id
            AND need.state IN ('answered','cancelled') AND need.execution_json=json_extract(h.safe_body_json,'$.binding')
            AND json_extract(h.safe_body_json,'$.decision') IN ('approve','deny','cancel')
            AND json_extract(h.safe_body_json,'$.binding.stepId')=attempt.step_id
            AND json_extract(h.safe_body_json,'$.binding.generation')=attempt.generation
            AND json_extract(h.safe_body_json,'$.binding.attempt')=attempt.attempt
            AND json_extract(h.safe_body_json,'$.binding.executionEpoch')=attempt.execution_epoch
            AND json_type(h.safe_body_json,'$.binding.commandIndex')='null'
            AND json_type(h.safe_body_json,'$.binding.effectId')='null'
            AND json_extract(h.safe_body_json,'$.binding.orderKey')=
              'workflow-gate/' || attempt.step_id || '/' || attempt.generation || '/' || attempt.attempt)
        """;

    /// <summary>Called only after the owning run commits cancellation/failure in this same writer.</summary>
    internal static IReadOnlyList<EventDraft> CancelForRun(IWriteScope s, CommandContext c, string runId)
    {
        var gates = s.Query("""
            SELECT st.id,st.version,st.attempt,st.generation,st.authority_epoch,n.id,n.version,n.execution_json,r.owner_session_id
            FROM engine_step st JOIN engine_run r ON r.id=st.run_id JOIN engine_needs_me n ON n.step_id=st.id
            JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt
            WHERE r.id=$run AND r.state IN ('cancelling','failed') AND r.definition_hash IS NOT NULL AND r.machine=$machine
              AND st.kind='gate' AND st.state='waiting' AND st.dispatch_state='gate' AND st.authority_machine=$machine
              AND st.session_id IS NULL AND st.workspace_id IS NULL AND st.authority_session_id IS NULL
              AND a.generation=st.generation AND a.execution_epoch=st.authority_epoch AND a.authority_machine=$machine
              AND a.session_id IS NULL AND a.workspace_id IS NULL AND a.gate_state='waiting'
              AND n.source_kind='gate' AND n.state='open' AND n.run_id=r.id AND n.session_id=r.owner_session_id
              AND n.addressee_session_id IS NULL AND n.machine=$machine AND n.provider_request_id IS NULL
              AND json_extract(n.execution_json,'$.stepId')=st.id AND json_extract(n.execution_json,'$.generation')=st.generation
              AND json_extract(n.execution_json,'$.attempt')=st.attempt AND json_extract(n.execution_json,'$.executionEpoch')=st.authority_epoch
              AND json_type(n.execution_json,'$.commandIndex')='null' AND json_type(n.execution_json,'$.effectId')='null'
              AND json_extract(n.execution_json,'$.orderKey')='workflow-gate/' || st.id || '/' || st.generation || '/' || st.attempt
            """, r => new
        {
            Step = r.Text(0),
            Version = r.Long(1),
            Attempt = r.Long(2),
            Generation = r.Long(3),
            Epoch = r.Long(4),
            Need = r.Text(5),
            NeedVersion = r.Long(6),
            Binding = r.Text(7),
            Owner = r.Text(8)
        },
            new SqlArg("$run", runId), new("$machine", c.Machine));
        List<EventDraft> events = [];
        foreach (var gate in gates)
        {
            var binding = JsonSerializer.Deserialize(gate.Binding, WorkflowGateJson.Default.WorkflowGateNeed)!;
            s.Execute("""
                UPDATE engine_needs_me SET state='cancelled',version=version+1,updated_ms=$now,answered_by=$actor,answered_ms=$now WHERE id=$need;
                UPDATE engine_step SET state='cancelled',version=version+1,updated_ms=$now,ended_ms=$now,outcome_reason='run_cancelled',dispatch_state='settled' WHERE id=$step;
                UPDATE engine_step_attempt SET state='cancelled',ended_ms=$now,reason='run_cancelled',gate_state='settled' WHERE step_id=$step AND attempt=$attempt;
                INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
                  SELECT $history,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,'gate_decided',$now,$body
                  FROM engine_workflow_history WHERE run_id=$run;
                """, new("$need", gate.Need), new("$now", c.NowMs), new("$actor", c.Actor.Id), new("$step", gate.Step), new("$attempt", gate.Attempt),
                new("$history", c.CommandId.ToString("N") + "/" + gate.Step + "/gate_cancelled"), new("$machine", c.Machine), new("$run", runId), new("$generation", gate.Generation),
                new("$body", JsonSerializer.Serialize(new WorkflowGateDecision(binding, gate.Need, "cancel", null), WorkflowGateJson.Default.WorkflowGateDecision)));
            events.Add(new("needs_me.cancelled", "need", gate.Need, gate.NeedVersion + 1, false, new(gate.Owner, runId, gate.Step, null, "cancelled", gate.Epoch)));
            events.Add(new("step.changed", "step", gate.Step, gate.Version + 1, false, new(null, runId, gate.Step, gate.Owner, "cancelled", gate.Epoch)));
        }
        return events;
    }
}
