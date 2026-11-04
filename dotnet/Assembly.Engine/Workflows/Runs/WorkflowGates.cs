using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Workflows.Scheduling;

namespace Assembly.Engine.Workflows.Runs;

/// <summary>Runs in the existing worker after recovery. Gate context is display data, never executable authority.</summary>
public static class WorkflowGates
{
    public static int OpenReady(IWriteScope s, string machine, long now)
    {
        var ready = s.Query($"""
            SELECT st.id,st.version,st.attempt,st.generation,st.run_id,r.owner_session_id,owner.epoch,
              json_extract(snap.snapshot_json,'$.snapshot.definition.steps.' || st.definition_key || '.gate')
            FROM engine_step st JOIN engine_run r ON r.id=st.run_id JOIN engine_session owner ON owner.id=r.owner_session_id
            JOIN engine_workflow_snapshot snap ON snap.run_id=r.id
            WHERE r.machine=$machine AND r.state IN ('queued','running','needs_me') AND r.paused=0 AND r.setup_state='ready'
              AND owner.machine=$machine AND owner.parent_id IS NULL AND owner.holder='local' AND owner.reserved_epoch IS NULL
              AND owner.state IN ('idle','running','needs_me') AND owner.cancel_requested=0
              AND st.kind='gate' AND st.state='pending' AND st.attempt_in_generation<st.max_attempts
              AND COALESCE(st.dispatch_state,'')<>'redo_pending' AND NOT EXISTS ({WorkflowSchedule.UnsettledDependency})
            ORDER BY r.created_ms,r.id,st.rowid LIMIT 100
            """, r => new { Step = r.Text(0), Version = r.Long(1), Attempt = r.Long(2) + 1, Generation = r.Long(3), Run = r.Text(4), Owner = r.Text(5), Epoch = r.Long(6), Text = r.IsNull(7) ? "Approve this workflow step?" : r.Text(7) }, new SqlArg("$machine", machine));
        foreach (var row in ready)
        {
            var c = new CommandContext(Guid.NewGuid(), machine, new(ActorKind.System, "engine.worker"), row.Step, row.Version, row.Epoch, now);
            var id = row.Step + "/gate/" + row.Attempt;
            var text = row.Text.Length > 4096 ? row.Text[..4096] : row.Text;
            var presentation = NeedPresentationInputs.Normalize(new(row.Owner, row.Run, row.Step, "workflow_gate", text, ["approve", "revise", "deny"], null, null, null));
            var number = NeedPresentationInputs.AllocateNumber(s);
            s.Execute("""
                UPDATE engine_step SET state='waiting',dispatch_state='gate',attempt=$attempt,attempt_in_generation=attempt_in_generation+1,
                  authority_epoch=$epoch,authority_machine=$machine,started_ms=$now,outcome_reason='owner_gate',version=version+1,updated_ms=$now WHERE id=$step;
                INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,late,generation,attempt_in_generation,execution_epoch,gate_state,authority_machine)
                  SELECT id,attempt,'waiting',$now,0,generation,attempt_in_generation,$epoch,'waiting',$machine FROM engine_step WHERE id=$step;
                INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,run_id,step_id,kind,text,options_json,state,
                  addressee_session_id,source_kind,presentation_json,display_number)
                VALUES($id,1,$machine,$now,$now,$owner,$run,$step,'workflow_gate',$text,'["approve","revise","deny"]','open',$owner,'workflow_gate',$presentation,$number);
                """, new("$attempt", row.Attempt), new("$now", now), new("$step", row.Step), new("$epoch", row.Epoch), new("$machine", machine),
                new("$id", id), new("$owner", row.Owner), new("$run", row.Run), new("$text", text), new("$presentation", JsonSerializer.Serialize(presentation, EngineJson.Default.NeedPresentation)), new("$number", number));
            List<EventDraft> events = [new("step.changed","step",row.Step,row.Version+1,false,new(null,row.Run,row.Step,null,"waiting",row.Epoch)),
                new("needs_me.opened","needs_me",id,1,false,new(row.Owner,row.Run,row.Step,row.Owner,"open",row.Epoch))];
            events.AddRange(RunTransitions.Recompute(s, c, row.Run));
            EngineJournal.Commit(s, c, row.Version + 1, row.Epoch, events, []);
        }
        return ready.Count;
    }
}
