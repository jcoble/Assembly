using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Workflows.Runs;

/// <summary>Only checked, same-head successful ancestors can be inherited after the new workspace is confirmed.</summary>
internal static class WorkflowRetrySeeds
{
    internal static IReadOnlyList<EventDraft> Apply(IWriteScope s, CommandContext c, string run)
    {
        var candidates = s.Query("""
            WITH RECURSIVE ancestors(id) AS (
              SELECT d.dependency_id FROM engine_step_dependency d JOIN engine_step failed ON failed.id=d.step_id
                JOIN engine_run r ON r.rerun_of=failed.run_id
                WHERE r.id=$run AND failed.state IN ('failed','cancelled')
              UNION SELECT d.dependency_id FROM engine_step_dependency d JOIN ancestors a ON d.step_id=a.id),
            valid AS (
              SELECT old.id,old.definition_key,receipt.id receipt_id,old.proof_json,old.outcome_reason
              FROM engine_run r JOIN engine_workflow_snapshot snap ON snap.run_id=r.id
              JOIN engine_run original ON original.id=r.rerun_of AND original.work_head=r.work_head
              JOIN engine_step old ON old.run_id=original.id JOIN engine_workflow_receipt receipt ON receipt.id=old.receipt_id
              WHERE r.id=$run AND r.setup_state='ready' AND r.work_head IS NOT NULL
                AND json_extract(snap.snapshot_json,'$.rerun.failedOnly')=1
                AND old.id IN ancestors AND old.state='succeeded' AND old.required=1
                AND receipt.state='accepted' AND receipt.observed_head=r.work_head AND receipt.checked_ms IS NOT NULL
                AND receipt.step_id=old.id AND receipt.run_id=original.id AND receipt.attempt=old.attempt AND receipt.generation=old.generation
                AND EXISTS(SELECT 1 FROM engine_workflow_history h WHERE h.run_id=original.id AND h.step_id=old.id
                  AND h.attempt=old.attempt AND h.generation=old.generation AND h.kind='receipt_checked'
                  AND json_extract(h.safe_body_json,'$.check.accepted')=1 AND json_extract(h.safe_body_json,'$.check.head')=r.work_head
                  AND json_extract(h.safe_body_json,'$.receiptId')=receipt.id)),
            invalid(id) AS (
              SELECT old.id FROM engine_step old JOIN engine_run r ON r.rerun_of=old.run_id
                WHERE r.id=$run AND old.id NOT IN (SELECT id FROM valid)
              UNION SELECT d.step_id FROM engine_step_dependency d JOIN invalid i ON d.dependency_id=i.id)
            SELECT fresh.id,fresh.version,valid.id,valid.receipt_id,valid.proof_json,valid.outcome_reason
            FROM valid JOIN engine_step fresh ON fresh.definition_key=valid.definition_key AND fresh.run_id=$run
            WHERE valid.id NOT IN invalid AND fresh.state='pending' AND fresh.attempt=0
            ORDER BY fresh.rowid
            """, r => new { Step = r.Text(0), Version = r.Long(1), SourceStep = r.Text(2), Receipt = r.Text(3), Proof = r.IsNull(4) ? null : r.Text(4), Reason = r.IsNull(5) ? null : r.Text(5) }, new SqlArg("$run", run));
        List<EventDraft> events = [];
        foreach (var row in candidates)
        {
            s.Execute("""
                UPDATE engine_step SET state='succeeded',dispatch_state='inherited',receipt_id=$receipt,proof_json=$proof,
                  outcome_reason=$reason,ended_ms=$now,version=version+1,updated_ms=$now WHERE id=$step;
                INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,generation,kind,at_ms,safe_body_json)
                SELECT $history,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,0,'retry_inherited',$now,
                  json_object('sourceStepId',$source,'sourceReceiptId',$receipt) FROM engine_workflow_history WHERE run_id=$run;
                """, new("$receipt", row.Receipt), new("$proof", row.Proof), new("$reason", row.Reason), new("$now", c.NowMs), new("$step", row.Step),
                new("$history", c.CommandId.ToString("N") + "/seed/" + row.Step), new("$machine", c.Machine), new("$run", run), new("$source", row.SourceStep));
            events.Add(new("step.changed", "step", row.Step, row.Version + 1, false, new(null, run, row.Step, null, "succeeded", null)));
        }
        return events;
    }
}
