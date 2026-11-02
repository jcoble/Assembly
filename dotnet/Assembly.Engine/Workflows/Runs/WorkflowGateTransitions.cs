using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Workflows.Scheduling;

namespace Assembly.Engine.Workflows.Runs;

public sealed record WorkflowGateNeed(string OrderKey, string StepId, int Generation, int Attempt,
    int? CommandIndex, long ExecutionEpoch, string? EffectId);
internal sealed record WorkflowGateDecision(WorkflowGateNeed Binding, string NeedId, string Decision, string? Reply);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowGateNeed))]
[JsonSerializable(typeof(WorkflowGateDecision))]
internal sealed partial class WorkflowGateJson : JsonSerializerContext;

/// <summary>Pure gates have a real attempt and owner-routed need, without a provider or executor.</summary>
public static partial class WorkflowGateTransitions
{
    internal static CommandResult Open(IWriteScope s, CommandContext c)
    {
        if (c.Actor.Kind != ActorKind.System) return Refuse(c, "forbidden");
        var row = s.QueryFirst($"""
            SELECT st.version,st.run_id,st.generation,st.attempt,st.attempt_in_generation,r.owner_session_id,o.epoch,
              json_extract(snapshot.snapshot_json,'$.snapshot.definition.steps.' || json_quote(st.definition_key) || '.gate')
            FROM engine_step st JOIN engine_run r ON r.id=st.run_id JOIN engine_session o ON o.id=r.owner_session_id
            JOIN engine_workflow_snapshot snapshot ON snapshot.run_id=r.id
            WHERE st.id=$step AND st.kind='gate' AND st.state='pending' AND st.session_id IS NULL
              AND st.workspace_id IS NULL AND st.authority_session_id IS NULL AND st.attempt_in_generation<st.max_attempts
              AND COALESCE(st.dispatch_state,'')<>'redo_pending' AND st.machine=$machine AND r.machine=$machine
              AND r.definition_hash IS NOT NULL AND r.state IN ('queued','running','needs_me') AND r.paused=0 AND r.setup_state='ready'
              AND o.machine=$machine AND o.parent_id IS NULL AND o.holder='local' AND o.reserved_epoch IS NULL
              AND o.cancel_requested=0 AND o.state IN ('idle','running','needs_me')
              AND NOT EXISTS ({WorkflowSchedule.UnsettledDependency})
              AND NOT EXISTS(SELECT 1 FROM engine_needs_me n WHERE n.step_id=st.id AND n.state='open')
            """, r => new
        {
            Version = r.Long(0),
            Run = r.Text(1),
            Generation = checked((int)r.Long(2)),
            Attempt = checked((int)r.Long(3)),
            Used = r.Long(4),
            Owner = r.Text(5),
            Epoch = r.Long(6),
            Gate = r.IsNull(7) ? null : r.Text(7)
        },
            new SqlArg("$step", c.EntityId), new("$machine", c.Machine));
        if (row is null) return Refuse(c, "gate_not_ready");
        if (row.Version != c.ExpectedVersion) return Refuse(c, "version_conflict");
        if (row.Epoch != c.Epoch) return Refuse(c, "stale_epoch");
        return EngineJournal.Execute(s, c, "\"workflow_gate_open/v1\"", () =>
        {
            var binding = new WorkflowGateNeed($"workflow-gate/{c.EntityId}/{row.Generation}/{row.Attempt + 1}", c.EntityId,
                row.Generation, row.Attempt + 1, null, row.Epoch, null);
            var need = binding.OrderKey + "/need";
            s.Execute("""
            UPDATE engine_step SET state='waiting',dispatch_state='gate',version=version+1,updated_ms=$now,
              attempt=attempt+1,attempt_in_generation=attempt_in_generation+1,authority_machine=$machine,authority_epoch=$epoch,
              outcome_reason='gate',started_ms=$now,ended_ms=NULL,reminder_sent=0,report_requested=0 WHERE id=$step;
            INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,late,generation,attempt_in_generation,authority_machine,execution_epoch,gate_state)
              VALUES($step,$attempt,'waiting',$now,0,$generation,$used,$machine,$epoch,'waiting');
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,run_id,step_id,kind,text,options_json,state,
              addressee_session_id,source_kind,execution_json)
              VALUES($need,1,$machine,$now,$now,$owner,$run,$step,'confirmation',$text,'["approve","deny"]','open',
                NULL,'gate',$binding);
            """, new("$step", c.EntityId), new("$now", c.NowMs), new("$machine", c.Machine), new("$epoch", row.Epoch),
                new("$attempt", binding.Attempt), new("$generation", row.Generation), new("$used", row.Used + 1), new("$need", need),
                new("$owner", row.Owner), new("$run", row.Run), new("$text", row.Gate ?? "Owner approval required."),
                new("$binding", JsonSerializer.Serialize(binding, WorkflowGateJson.Default.WorkflowGateNeed)));
            List<EventDraft> events = [new("needs_me.opened", "need", need, 1, false, new(row.Owner, row.Run, c.EntityId, null, "open", row.Epoch)),
            new("step.changed", "step", c.EntityId, row.Version + 1, false, new(null, row.Run, c.EntityId, row.Owner, "waiting", row.Epoch))];
            events.AddRange(RunTransitions.Recompute(s, c, row.Run));
            return EngineJournal.Commit(s, c, row.Version + 1, row.Epoch, events, []);
        });
    }

    internal static CommandResult Decide(IWriteScope s, CommandContext c, string decision, string? reply)
    {
        var need = s.QueryFirst("""
            SELECT n.id FROM engine_needs_me n JOIN engine_step st ON st.id=n.step_id
            WHERE st.id=$step AND n.source_kind='gate' AND json_extract(n.execution_json,'$.generation')=st.generation
              AND json_extract(n.execution_json,'$.attempt')=st.attempt ORDER BY n.created_ms DESC,n.id DESC LIMIT 1
            """,
            r => r.Text(0), new SqlArg("$step", c.EntityId));
        return need is null ? Refuse(c, "gate_not_waiting") : DecideCore(s, c, need, decision, reply, false);
    }

    /// <summary>The prepared owner-answer boundary calls this same gate decision core; no provider answer is emitted.</summary>
    public static CommandResult ResolveOwnerNeed(IWriteScope s, CommandContext c, ResolveNeed answer)
    {
        if (answer.State != "answered" || answer.Answer is not { ValueKind: JsonValueKind.String } value)
            return Refuse(c, "bad_args");
        return DecideCore(s, c, c.EntityId, value.GetString()!, null, true);
    }

    private static CommandResult DecideCore(IWriteScope s, CommandContext c, string needId, string decision, string? reply, bool needContext)
    {
        if (c.Actor.Kind != ActorKind.Window || c.Actor.Id != "owner") return Refuse(c, "forbidden");
        if (decision is not ("approve" or "deny")) return Refuse(c, "invalid_decision");
        if (reply?.Length > 16_384) return Refuse(c, "bad_args");
        var row = s.QueryFirst("""
            SELECT n.version,n.state,n.execution_json,st.id,st.version,st.run_id,st.generation,st.attempt,st.authority_epoch,
              r.owner_session_id,o.epoch,st.state,a.gate_state,a.execution_epoch,a.authority_machine
            FROM engine_needs_me n JOIN engine_step st ON st.id=n.step_id JOIN engine_run r ON r.id=st.run_id
            JOIN engine_session o ON o.id=r.owner_session_id JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt
            WHERE n.id=$need AND n.source_kind='gate' AND n.run_id=r.id AND n.session_id=o.id AND n.addressee_session_id IS NULL
              AND n.provider_request_id IS NULL AND n.machine=$machine AND r.machine=$machine AND st.machine=$machine
              AND st.kind='gate' AND st.session_id IS NULL AND st.workspace_id IS NULL AND st.authority_session_id IS NULL
              AND st.authority_machine=$machine AND a.session_id IS NULL AND a.workspace_id IS NULL AND a.generation=st.generation
              AND r.definition_hash IS NOT NULL AND r.state IN ('queued','running','needs_me')
              AND o.machine=$machine AND o.parent_id IS NULL AND o.holder='local' AND o.reserved_epoch IS NULL
              AND o.cancel_requested=0 AND o.state IN ('idle','running','needs_me')
            """, r => new
        {
            NeedVersion = r.Long(0),
            NeedState = r.Text(1),
            Binding = r.Text(2),
            Step = r.Text(3),
            Version = r.Long(4),
            Run = r.Text(5),
            Generation = checked((int)r.Long(6)),
            Attempt = checked((int)r.Long(7)),
            Epoch = r.Long(8),
            Owner = r.Text(9),
            OwnerEpoch = r.Long(10),
            State = r.Text(11),
            Gate = r.Text(12),
            AttemptEpoch = r.Long(13),
            Machine = r.Text(14)
        },
            new SqlArg("$need", needId), new("$machine", c.Machine));
        if (row is null) return Refuse(c, "gate_not_waiting");
        if (row.NeedState != "open") return Refuse(c, "already_resolved");
        if (c.EntityId != (needContext ? needId : row.Step)) return Refuse(c, "bad_args");
        if (c.ExpectedVersion != (needContext ? row.NeedVersion : row.Version)) return Refuse(c, "version_conflict");
        if (c.Epoch != row.OwnerEpoch) return Refuse(c, "stale_epoch");
        var binding = JsonSerializer.Deserialize(row.Binding, WorkflowGateJson.Default.WorkflowGateNeed)!;
        if (row.State != "waiting" || row.Gate != "waiting" || binding.StepId != row.Step || binding.Generation != row.Generation ||
            binding.Attempt != row.Attempt || binding.ExecutionEpoch != row.Epoch || binding.ExecutionEpoch != row.AttemptEpoch ||
            row.Machine != c.Machine || binding.CommandIndex is not null || binding.EffectId is not null ||
            binding.OrderKey != $"workflow-gate/{row.Step}/{row.Generation}/{row.Attempt}") return Refuse(c, "stale_attempt");
        var state = decision == "approve" ? "succeeded" : "failed";
        var body = JsonSerializer.Serialize(new WorkflowGateDecision(binding, needId, decision, reply), WorkflowGateJson.Default.WorkflowGateDecision);
        s.Execute("""
            UPDATE engine_needs_me SET state='answered',version=version+1,updated_ms=$now,answer_json=$answer,answered_by=$actor,answered_ms=$now WHERE id=$need;
            UPDATE engine_step SET state=$state,version=version+1,updated_ms=$now,ended_ms=$now,outcome_reason=$decision,dispatch_state='settled' WHERE id=$step;
            UPDATE engine_step_attempt SET state=$state,ended_ms=$now,reason=$decision,gate_state='settled' WHERE step_id=$step AND attempt=$attempt;
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
              SELECT $history,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,'gate_decided',$now,$body
              FROM engine_workflow_history WHERE run_id=$run;
            """, new("$need", needId), new("$now", c.NowMs), new("$answer", JsonSerializer.Serialize(decision, WorkflowControlJson.Default.String)),
            new("$actor", c.Actor.Id), new("$state", state), new("$decision", decision), new("$step", row.Step), new("$attempt", row.Attempt),
            new("$history", c.CommandId.ToString("N") + "/gate_decided"), new("$machine", c.Machine), new("$run", row.Run), new("$generation", row.Generation), new("$body", body));
        List<EventDraft> events = [new("needs_me.answered", "need", needId, row.NeedVersion + 1, false, new(row.Owner, row.Run, row.Step, null, "answered", row.OwnerEpoch)),
            new("step.changed", "step", row.Step, row.Version + 1, false, new(null, row.Run, row.Step, row.Owner, state, row.Epoch))];
        events.AddRange(RunTransitions.Recompute(s, c, row.Run));
        return EngineJournal.Commit(s, c, needContext ? row.NeedVersion + 1 : row.Version + 1, row.OwnerEpoch, events, []);
    }

    private static CommandResult Refuse(CommandContext c, string error) => WorkflowControlTransitions.Refuse(c, error);
}
