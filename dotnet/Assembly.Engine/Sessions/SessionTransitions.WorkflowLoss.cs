using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Sessions;

public static partial class SessionTransitions
{
    private static IReadOnlyList<EventDraft> WaitForLostWorkflowWorker(IWriteScope scope, CommandContext context)
    {
        var step = scope.QueryFirst("""
            SELECT st.id,st.run_id,st.version,st.attempt,st.generation,run.owner_session_id
            FROM engine_step st JOIN engine_run run ON run.id=st.run_id
            JOIN engine_step_attempt attempt ON attempt.step_id=st.id AND attempt.attempt=st.attempt AND attempt.generation=st.generation
            JOIN engine_session worker ON worker.id=st.session_id
            JOIN engine_workflow_snapshot snapshot ON snapshot.run_id=run.id AND snapshot.definition_hash=run.definition_hash
            WHERE run.machine=$machine AND run.state NOT IN ('succeeded','failed','cancelled','cancelling')
              AND st.kind IN ('agent','review') AND st.state IN ('running','waiting') AND attempt.state IN ('running','waiting')
              AND COALESCE(st.dispatch_state,'') NOT IN ('redo_pending','review_settled')
              AND st.session_id=$session AND st.authority_session_id=$session AND st.authority_epoch=$epoch
              AND st.authority_machine=$machine AND attempt.session_id=$session AND attempt.execution_epoch=$epoch
              AND attempt.authority_machine=$machine AND worker.epoch=$epoch AND worker.machine=$machine
              AND worker.state='lost' AND worker.holder='local' AND worker.reserved_epoch IS NULL AND worker.run_id=run.id
              AND EXISTS(SELECT 1 FROM json_each(snapshot.snapshot_json,'$.stepIds') ids WHERE ids.key=st.definition_key AND ids.value=st.id)
              AND EXISTS(SELECT 1 FROM json_each(snapshot.snapshot_json,'$.snapshot.definition.steps') spec
                WHERE spec.key=st.definition_key AND json_extract(spec.value,'$.kind')=st.kind)
            """, row => new { Id = row.Text(0), Run = row.Text(1), Version = row.Long(2), Attempt = row.Long(3), Generation = row.Long(4), Owner = row.Text(5) },
            new SqlArg("$machine", context.Machine), new("$session", context.EntityId), new("$epoch", context.Epoch));
        if (step is null) return [];
        scope.Execute("""
            UPDATE engine_step SET state='waiting',outcome_reason='session_lost',version=version+1,updated_ms=$now WHERE id=$step;
            UPDATE engine_step_attempt SET state='waiting',reason='session_lost' WHERE step_id=$step AND attempt=$attempt AND generation=$generation;
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
            SELECT $history,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,'session_lost',$now,
              json_object('stepId',$step,'attempt',$attempt,'generation',$generation,'sessionId',$session,'epoch',$epoch,'authorityMachine',$machine)
            FROM engine_workflow_history WHERE run_id=$run;
            """, new("$step", step.Id), new("$run", step.Run), new("$attempt", step.Attempt), new("$generation", step.Generation),
            new("$now", context.NowMs), new("$machine", context.Machine), new("$history", Guid.NewGuid().ToString("N")),
            new("$session", context.EntityId), new("$epoch", context.Epoch));
        return [new("step.changed", "step", step.Id, step.Version + 1, false,
            new(context.EntityId, step.Run, step.Id, step.Owner, "waiting", context.Epoch, "session_lost"))];
    }
}
