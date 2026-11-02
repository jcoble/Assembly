using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Workflows.Execution;

public static partial class CommandStepTransitions
{
    internal static CommandResult Timeout(IWriteScope s, CommandContext c)
    {
        CommandResult Refuse(string reason) => new(false, false, reason, c.ExpectedVersion, 0, c.Epoch);
        if (c.Actor.Kind != ActorKind.System) return Refuse("forbidden");
        var row = s.QueryFirst("""
            SELECT st.version,st.run_id,st.attempt,a.execution_epoch FROM engine_step st
            JOIN engine_run r ON r.id=st.run_id JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt
            WHERE st.id=$step AND st.kind='command' AND r.machine=$machine AND st.authority_machine=$machine
              AND st.state IN ('running','waiting') AND st.deadline_ms<=$now AND a.cancel_requested=0
              AND a.generation=st.generation AND a.state IN ('running','waiting')
              AND st.dispatch_state IS NOT 'redo_pending' AND r.state NOT IN ('succeeded','failed','cancelled','cancelling')
            """, r => (Version: r.Long(0), Run: r.Text(1), Attempt: r.Long(2), Epoch: r.Long(3)),
            new SqlArg("$step", c.EntityId), new("$machine", c.Machine), new("$now", c.NowMs));
        if (row.Run is null) return Refuse("not_due");
        if (row.Version != c.ExpectedVersion) return Refuse("version_conflict");
        if (row.Epoch != c.Epoch) return Refuse("stale_epoch");
        s.Execute("""
            UPDATE engine_step_attempt SET cancel_requested=1,reason='timed_out' WHERE step_id=$step AND attempt=$attempt;
            UPDATE engine_step SET state='waiting',dispatch_state='timeout_pending',deadline_ms=NULL,
              outcome_reason='timed_out',version=version+1,updated_ms=$now WHERE id=$step;
            """, new("$step", c.EntityId), new("$attempt", row.Attempt), new("$now", c.NowMs));
        var settled = s.QueryFirst("""
            SELECT o.id,o.payload_json,o.order_key,o.attempt FROM engine_outbox o JOIN engine_step_attempt a
              ON a.step_id=json_extract(o.payload_json,'$.stepId') AND a.attempt=json_extract(o.payload_json,'$.attempt')
            WHERE a.step_id=$step AND a.attempt=$attempt AND a.gate_state='ready' AND o.kind='workflow_command' AND o.state='done'
              AND json_extract(o.payload_json,'$.commandIndex')=a.command_index-1
            ORDER BY o.event_seq DESC LIMIT 1
            """, r => (Id: r.Text(0), Payload: r.Text(1), Key: r.Text(2), Attempt: checked((int)r.Long(3))),
            new SqlArg("$step", c.EntityId), new("$attempt", row.Attempt));
        if (settled.Id is not null && ExecutionsDrained(s, row.Run, c.EntityId, checked((int)row.Attempt)))
        {
            s.Execute("UPDATE engine_step_attempt SET state='waiting',gate_state='settled',ended_ms=$now WHERE step_id=$step AND attempt=$attempt; UPDATE engine_step SET dispatch_state='settled',ended_ms=$now WHERE id=$step",
                new("$now", c.NowMs), new("$step", c.EntityId), new("$attempt", row.Attempt));
            var intent = JsonSerializer.Deserialize(settled.Payload, WorkflowExecutionJson.Default.CommandExecutionIntent)!;
            using var document = JsonDocument.Parse(settled.Payload);
            WorkflowExecutionNeeds.OpenTimeout(s, c, new(settled.Id, "workflow_command", null, row.Run,
                document.RootElement.Clone(), settled.Attempt, settled.Key), intent);
        }
        List<EventDraft> events = [new("workflow.command_timeout_requested", "step", c.EntityId, row.Version + 1, false,
            new(null, row.Run, c.EntityId, null, "waiting", c.Epoch))];
        events.AddRange(RunTransitions.Recompute(s, c, row.Run));
        return EngineJournal.Commit(s, c, row.Version + 1, c.Epoch, events, []);
    }

    /// <summary>Consumes one pushed cancellation for an unavailable effect; uncertainty never becomes a launch.</summary>
    internal static CommandResult ReconcileStop(IWriteScope s, CommandContext c)
    {
        CommandResult Refuse(string reason) => new(false, false, reason, c.ExpectedVersion, 0, c.Epoch);
        if (c.Actor.Kind != ActorKind.System) return Refuse("forbidden");
        var row = s.QueryFirst("""
            SELECT o.version,o.payload_json FROM engine_outbox o JOIN engine_run r ON r.id=o.run_id
            JOIN engine_step st ON st.id=json_extract(o.payload_json,'$.stepId')
            JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=json_extract(o.payload_json,'$.attempt')
            WHERE o.id=$effect AND o.machine=$machine AND r.machine=$machine AND o.kind='workflow_command' AND o.state='unavailable'
              AND (a.cancel_requested=1 OR r.state IN ('cancelling','cancelled','failed') OR st.generation!=a.generation OR st.attempt!=a.attempt)
              AND NOT EXISTS(SELECT 1 FROM engine_workflow_history h WHERE h.run_id=r.id AND h.kind='command_stop_requested'
                AND json_extract(h.safe_body_json,'$.effectId')=o.id)
            """, r => (Version: r.Long(0), Payload: r.Text(1)), new SqlArg("$effect", c.EntityId), new("$machine", c.Machine));
        if (row.Payload is null) return Refuse("stop_not_pending");
        if (row.Version != c.ExpectedVersion) return Refuse("version_conflict");
        var intent = JsonSerializer.Deserialize(row.Payload, WorkflowExecutionJson.Default.CommandExecutionIntent)!;
        if (c.Epoch != intent.ExecutionEpoch) return Refuse("stale_epoch");
        s.Execute("UPDATE engine_outbox SET state='uncertain',version=version+1,updated_ms=$now,next_ms=NULL WHERE id=$effect",
            new("$now", c.NowMs), new("$effect", c.EntityId));
        History(s, c, intent, "command_stop_requested", "{\"effectId\":" + JsonSerializer.Serialize(c.EntityId, EngineJson.Default.String) + "}");
        return EngineJournal.Commit(s, c, row.Version + 1, c.Epoch,
            [new("effect.changed", "effect", c.EntityId, row.Version + 1, false,
                new(null, intent.RunId, intent.StepId, null, "uncertain", intent.ExecutionEpoch))], []);
    }

    /// <summary>Only observed exits, closed no-launch facts, or trusted absence authorize cancellation completion.</summary>
    public static bool ExecutionsDrained(IReadScope s, string runId, string? stepId = null, int? attempt = null) => s.QueryFirst("""
        SELECT NOT EXISTS(SELECT 1 FROM engine_outbox o WHERE o.run_id=$run AND o.kind='workflow_command'
          AND ($step IS NULL OR json_extract(o.payload_json,'$.stepId')=$step AND json_extract(o.payload_json,'$.attempt')=$attempt)
          AND NOT COALESCE((
            json_extract(o.receipt_json,'$.disposition')=0 AND json_extract(o.receipt_json,'$.data.effectId')=o.id
            AND (json_extract(o.receipt_json,'$.data.exitCode') IS NOT NULL
              OR json_extract(o.receipt_json,'$.data.notStarted')=1 OR json_extract(o.receipt_json,'$.data.executorAbsent')=1)
            OR EXISTS(SELECT 1 FROM engine_workflow_history h WHERE h.run_id=$run AND h.kind IN ('command_retry_authorized')
              AND h.step_id=json_extract(o.payload_json,'$.stepId') AND h.generation=json_extract(o.payload_json,'$.generation')
              AND h.attempt=json_extract(o.payload_json,'$.attempt'))
            OR o.executor_json IS NULL AND EXISTS(SELECT 1 FROM engine_workflow_policy p WHERE p.run_id=$run
              AND p.step_id=json_extract(o.payload_json,'$.stepId') AND p.decision='deny'
              AND o.last_error='policy_denied' AND EXISTS(SELECT 1 FROM engine_needs_me n WHERE n.id=p.need_id
                AND json_extract(n.execution_json,'$.effectId')=o.id))),0))
        """, r => r.Long(0) != 0, new SqlArg("$run", runId), new("$step", stepId), new("$attempt", attempt));
}
