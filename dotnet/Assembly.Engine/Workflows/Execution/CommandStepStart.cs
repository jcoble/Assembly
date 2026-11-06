using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows.Policy;

namespace Assembly.Engine.Workflows.Execution;

public static partial class CommandStepTransitions
{
    internal static CommandResult Start(IWriteScope s, CommandContext c, PreparedCommandStep prepared)
    {
        var b = prepared.Body; var i = b.Intent;
        CommandResult Refuse(string reason) => new(false, false, reason, c.ExpectedVersion, 0, c.Epoch);
        if (b.Context != c with { NowMs = 0 } || c.EntityId != i.StepId || c.Actor.Kind != ActorKind.System) return Refuse("forbidden");
        var ready = s.QueryFirst("""
            SELECT st.state,st.attempt,st.generation,st.version,r.version,r.definition_hash,se.epoch,
              COALESCE(a.command_index,0),a.gate_state,COALESCE(a.execution_epoch,0),se.version,w.version
            FROM engine_step st JOIN engine_run r ON r.id=st.run_id JOIN engine_session se ON se.id=r.owner_session_id
            JOIN engine_workflow_workspace w ON w.id=st.workspace_id AND w.state='ready'
            LEFT JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt
            WHERE st.id=$step AND st.run_id=$run AND st.kind='command' AND st.dispatch_state IS NOT 'redo_pending' AND r.machine=$machine AND st.authority_machine=$machine
              AND (st.state='pending' OR st.deadline_ms IS NULL OR st.deadline_ms>$now)
              AND w.path=$cwd AND r.setup_state='ready' AND (r.paused=0 OR st.state='running') AND r.state NOT IN ('succeeded','failed','cancelled','cancelling')
              AND se.holder='local' AND se.reserved_epoch IS NULL AND se.state NOT IN ('foreign','ended','handing_off','lost')
              AND NOT EXISTS(SELECT 1 FROM engine_step_dependency d JOIN engine_step dep ON dep.id=d.dependency_id
                WHERE d.step_id=st.id AND dep.state NOT IN ('succeeded','skipped'))
            """, r => new
        {
            State = r.Text(0),
            Attempt = checked((int)r.Long(1)),
            Generation = checked((int)r.Long(2)),
            Version = r.Long(3),
            RunVersion = r.Long(4),
            Hash = r.Text(5),
            Epoch = r.Long(6),
            Index = checked((int)r.Long(7)),
            Gate = r.IsNull(8) ? null : r.Text(8),
            ExecutionEpoch = r.Long(9),
            OwnerVersion = r.Long(10),
            WorkspaceVersion = r.Long(11)
        },
            new SqlArg("$step", i.StepId), new("$run", i.RunId), new("$machine", c.Machine), new("$cwd", i.Cwd), new("$now", c.NowMs));
        if (ready is null) return Refuse("command_not_ready");
        if (ready.Version != c.ExpectedVersion || b.StepVersion != ready.Version || b.RunVersion != ready.RunVersion ||
            b.OwnerVersion != ready.OwnerVersion || b.WorkspaceVersion != ready.WorkspaceVersion ||
            !b.Dependencies.SequenceEqual(CommandStepPreparation.Dependencies(s, i.StepId))) return Refuse("version_conflict");
        if (ready.Epoch != c.Epoch || ready.Hash != b.DefinitionHash || ready.Generation != i.Generation) return Refuse("stale_attempt");
        if (i.PrBody is null || i.PrBody.Generation != i.Generation ||
            i.PrBody.InputSetSha256 != WorkflowPrBodies.InputSetHash(s, i.RunId, i.StepId, i.Generation)) return Refuse("pr_body_stale");
        var first = ready.State == "pending";
        if (first ? i.Attempt != ready.Attempt + 1 || i.CommandIndex != 0 || i.ExecutionEpoch != ready.ExecutionEpoch + 1
            : ready.State != "running" || ready.Gate != "ready" || i.Attempt != ready.Attempt || i.CommandIndex != ready.Index || i.ExecutionEpoch != ready.ExecutionEpoch)
            return Refuse("stale_attempt");
        if (s.QueryFirst("""
            SELECT COUNT(*) FROM engine_outbox WHERE order_key=$key AND state NOT IN ('done','failed','dead')
            """, r => r.Long(0), new SqlArg("$key", i.OrderKey)) != 0) return Refuse("command_executor_owned");
        var denied = b.Decision == PolicyDecision.Deny;
        if (first)
            s.Execute("""
                INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,late,generation,attempt_in_generation,
                  execution_epoch,command_index,cancel_requested,fingerprint,gate_state,authority_machine,workspace_id)
                SELECT id,$attempt,$state,$now,0,generation,attempt_in_generation+1,$epoch,0,0,$fingerprint,$gate,$machine,workspace_id
                FROM engine_step WHERE id=$step
                """, new("$step", i.StepId), new("$attempt", i.Attempt), new("$state", denied ? "failed" : "running"), new("$now", c.NowMs),
                new("$epoch", i.ExecutionEpoch), new("$fingerprint", i.Fingerprint), new("$gate", denied ? "settled" : "executing"), new("$machine", c.Machine));
        else
            s.Execute("UPDATE engine_step_attempt SET gate_state=$gate,state=$state WHERE step_id=$step AND attempt=$attempt",
                new("$gate", denied ? "settled" : "executing"), new("$state", denied ? "failed" : "running"), new("$step", i.StepId), new("$attempt", i.Attempt));
        s.Execute("""
            UPDATE engine_step SET state=$state,attempt=$attempt,attempt_in_generation=attempt_in_generation+$first,
              version=version+1,updated_ms=$now,started_ms=COALESCE(started_ms,$now),authority_epoch=$epoch,
              ended_ms=$ended,outcome_reason=$reason,dispatch_state=$dispatch,
              deadline_ms=CASE WHEN $ended IS NOT NULL THEN NULL WHEN $first=1
                THEN $now+(SELECT step_timeout_ms FROM engine_run WHERE id=$run) ELSE deadline_ms END WHERE id=$step;
            INSERT INTO engine_workflow_policy(id,version,machine,created_ms,updated_ms,run_id,step_id,command_hash,decision)
            VALUES($policy,1,$machine,$now,$now,$run,$step,$hash,$decision);
            """, new("$state", denied ? "failed" : "running"), new("$attempt", i.Attempt), new("$first", first ? 1 : 0),
            new("$now", c.NowMs), new("$epoch", i.ExecutionEpoch), new("$ended", denied ? c.NowMs : null), new("$reason", denied ? "policy_denied" : null),
            new("$dispatch", denied ? "settled" : "running"), new("$step", i.StepId), new("$policy", c.CommandId.ToString("N") + "/policy"),
            new("$machine", c.Machine), new("$run", i.RunId), new("$hash", Digest(JsonSerializer.Serialize(i, WorkflowExecutionJson.Default.CommandExecutionIntent))),
            new("$decision", b.Decision.ToString().ToLowerInvariant()));
        History(s, c, i, denied ? "command_denied" : "command_started", "{}");
        var effect = new EffectDraft("", "workflow_command", null, i.RunId,
            JsonSerializer.SerializeToElement(i, WorkflowExecutionJson.Default.CommandExecutionIntent), i.OrderKey);
        if (denied)
            s.Execute("UPDATE engine_step_attempt SET ended_ms=$now,reason='policy_denied' WHERE step_id=$step AND attempt=$attempt",
                new("$now", c.NowMs), new("$step", i.StepId), new("$attempt", i.Attempt));
        List<EventDraft> events = [new("step.changed", "step", i.StepId, ready.Version + 1, false,
            new(null, i.RunId, i.StepId, null, denied ? "failed" : "running", i.ExecutionEpoch))];
        events.AddRange(Assembly.Engine.Runs.RunTransitions.Recompute(s, c, i.RunId));
        return EngineJournal.Commit(s, c, ready.Version + 1, c.Epoch, events, denied ? [] : [effect]);
    }
}
