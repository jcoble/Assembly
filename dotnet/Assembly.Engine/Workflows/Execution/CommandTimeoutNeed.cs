using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.NeedsMe;

namespace Assembly.Engine.Workflows.Execution;

public static partial class WorkflowExecutionNeeds
{
    internal static void OpenTimeout(IWriteScope s, CommandContext c, Effect effect, CommandExecutionIntent i)
    {
        var owner = s.QueryFirst("SELECT owner_session_id FROM engine_run WHERE id=$run", r => r.Text(0), new SqlArg("$run", i.RunId));
        var id = effect.Id + "/timed_out";
        var binding = new WorkflowExecutionNeed(i.OrderKey, i.StepId, i.Generation, i.Attempt, i.CommandIndex, i.ExecutionEpoch, effect.Id, "timed_out");
        var presentation = NeedPresentationInputs.Normalize(new(owner!, i.RunId, i.StepId, "command_timeout",
            "Step timed out and its executor stopped; waiting for owner.", ["retry", "cancel"], null, null, null));
        var displayNumber = NeedPresentationInputs.AllocateNumber(s);
        s.Execute("""
            UPDATE engine_needs_me SET state='cancelled',version=version+1,updated_ms=$now WHERE source_kind='workflow'
              AND run_id=$run AND step_id=$step AND state='open';
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,run_id,step_id,kind,text,options_json,state,
              addressee_session_id,source_kind,execution_json,presentation_json,display_number)
            VALUES($id,1,$machine,$now,$now,$owner,$run,$step,'command_timeout','Step timed out and its executor stopped; waiting for owner.',
              '["retry","cancel"]','open',$owner,'workflow',$binding,$presentation,$display);
            """, new("$now", c.NowMs), new("$run", i.RunId), new("$step", i.StepId), new("$id", id), new("$machine", c.Machine),
            new("$owner", owner), new("$presentation", JsonSerializer.Serialize(presentation, EngineJson.Default.NeedPresentation)),
            new("$display", displayNumber), new("$binding", JsonSerializer.Serialize(binding, WorkflowNeedJson.Default.WorkflowExecutionNeed)));
        EngineJournal.Commit(s, c, c.ExpectedVersion, c.Epoch,
            [new("needs_me.opened", "needs_me", id, 1, false, new(owner, i.RunId, i.StepId, owner, "open", c.Epoch))], []);
    }
}

public static partial class CommandStepTransitions
{
    /// <summary>Task10 calls only after its prepared owner/root control authorization; the old attempt is never resumed.</summary>
    internal static CommandResult AuthorizeTimeoutRetry(IWriteScope s, CommandContext c, string needId)
    {
        CommandResult Refuse(string reason) => new(false, false, reason, c.ExpectedVersion, 0, c.Epoch);
        var row = s.QueryFirst("""
            SELECT st.version,st.run_id,st.attempt,st.generation,a.execution_epoch,n.execution_json,n.version,owner.id,owner.epoch
            FROM engine_step st JOIN engine_run r ON r.id=st.run_id JOIN engine_session owner ON owner.id=r.owner_session_id
            JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt
            JOIN engine_needs_me n ON n.step_id=st.id AND n.id=$need AND n.state='open' AND n.source_kind='workflow'
            WHERE st.id=$step AND st.kind='command' AND st.state='waiting' AND st.outcome_reason='timed_out'
              AND st.authority_machine=$machine AND owner.machine=$machine AND a.reason='timed_out' AND a.gate_state='settled' AND a.generation=st.generation AND r.machine=$machine
              AND r.state NOT IN ('succeeded','failed','cancelled','cancelling') AND owner.holder='local' AND owner.reserved_epoch IS NULL
              AND owner.state NOT IN ('ended','lost','foreign','handing_off') AND owner.parent_id IS NULL
              AND n.session_id=owner.id AND n.addressee_session_id=owner.id
            """, r => new
        {
            Version = r.Long(0),
            Run = r.Text(1),
            Attempt = checked((int)r.Long(2)),
            Generation = checked((int)r.Long(3)),
            Epoch = r.Long(4),
            Binding = r.Text(5),
            NeedVersion = r.Long(6),
            Owner = r.Text(7),
            OwnerEpoch = r.Long(8)
        },
            new SqlArg("$step", c.EntityId), new("$need", needId), new("$machine", c.Machine));
        if (row is null) return Refuse("timeout_not_settled");
        if (!(c.Actor is { Kind: ActorKind.Window, Id: "owner" } || c.Actor.Kind == ActorKind.Session && c.Actor.Id == row.Owner)) return Refuse("forbidden");
        if (c.ExpectedVersion != row.Version) return Refuse("version_conflict");
        if (c.Epoch != row.OwnerEpoch) return Refuse("stale_epoch");
        var binding = JsonSerializer.Deserialize(row.Binding, WorkflowNeedJson.Default.WorkflowExecutionNeed)!;
        if (binding.Reason != "timed_out" || binding.StepId != c.EntityId || binding.Attempt != row.Attempt || binding.Generation != row.Generation ||
            binding.ExecutionEpoch != row.Epoch || binding.OrderKey != $"workflow-command/{c.EntityId}/{row.Generation}/{row.Attempt}" || !ExecutionsDrained(s, row.Run, c.EntityId, row.Attempt) || s.QueryFirst("""
                SELECT COUNT(*) FROM engine_outbox WHERE id=$effect AND state='done' AND kind='workflow_command'
                  AND order_key=$key AND run_id=$run AND json_extract(payload_json,'$.stepId')=$step
                  AND json_extract(payload_json,'$.generation')=$generation AND json_extract(payload_json,'$.attempt')=$attempt
                  AND json_extract(payload_json,'$.executionEpoch')=$epoch AND json_extract(payload_json,'$.commandIndex')=$index
                """, r => r.Long(0), new SqlArg("$effect", binding.EffectId), new("$key", binding.OrderKey), new("$index", binding.CommandIndex), new("$run", row.Run), new("$step", c.EntityId),
                    new("$generation", row.Generation), new("$attempt", row.Attempt), new("$epoch", row.Epoch)) != 1)
            return Refuse("executor_quiescence_required");
        s.Execute("""
            UPDATE engine_needs_me SET state='answered',version=version+1,updated_ms=$now,answer_json='"retry"',answered_by=$actor,answered_ms=$now,answered_origin_json=$origin WHERE id=$need;
            UPDATE engine_step SET state='pending',dispatch_state='pending',outcome_reason=NULL,ended_ms=NULL,deadline_ms=NULL,
              generation=generation+1,attempt_in_generation=0,scope_granted=0,
              version=version+1,updated_ms=$now WHERE id=$step;
            """, new("$now", c.NowMs), new("$actor", c.Actor.Id), new("$origin", NeedTransitions.OriginJson(c)), new("$need", needId), new("$step", c.EntityId));
        List<EventDraft> events = [new("needs_me.answered", "needs_me", needId, row.NeedVersion + 1, false, new(row.Owner, row.Run, c.EntityId, row.Owner, "answered", c.Epoch)),
            new("step.changed", "step", c.EntityId, row.Version + 1, false, new(null, row.Run, c.EntityId, null, "pending", row.Epoch))];
        events.AddRange(RunTransitions.Recompute(s, c, row.Run));
        return EngineJournal.Commit(s, c, row.Version + 1, c.Epoch, events, []);
    }
}
