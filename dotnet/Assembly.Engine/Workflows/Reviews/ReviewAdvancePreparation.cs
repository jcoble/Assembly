using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Workflows.Reviews;

internal sealed record RedoAdvanceTarget(string Run, long Version, int Generation, int Round, int Max);
internal sealed record RedoAdvancePreparation(RedoAdvanceTarget Target, IReadOnlyList<RedoRequest> Requests,
    IReadOnlyList<WorkflowExecutionFence> Affected);

public static partial class ReviewTransitions
{
    internal static RedoAdvancePreparation? PrepareAdvance(IReadScope s, CommandContext c,
        Func<IReadScope, WorkflowExecutionFence, bool> released, out string? error)
    {
        string? refusal = null;
        RedoAdvancePreparation? Refuse(string reason) { refusal = reason; return null; }
        var result = Read();
        error = refusal;
        return result;
        RedoAdvancePreparation? Read()
        {
            if (c.Actor.Kind != ActorKind.System) return Refuse("forbidden");
            var target = s.QueryFirst("""
                SELECT st.run_id,st.version,st.generation,st.round,r.max_rounds FROM engine_step st JOIN engine_run r ON r.id=st.run_id
                WHERE st.id=$target AND st.dispatch_state='redo_pending' AND r.machine=$machine
                  AND r.state NOT IN ('succeeded','failed','cancelled','cancelling') AND r.paused=0
                """, r => new RedoAdvanceTarget(r.Text(0), r.Long(1), checked((int)r.Long(2)), checked((int)r.Long(3)), checked((int)r.Long(4))),
                new SqlArg("$target", c.EntityId), new("$machine", c.Machine));
            if (target is null) return Refuse("redo_not_pending");
            if (target.Version != c.ExpectedVersion) return Refuse("version_conflict");
            var requests = s.Query("""
                SELECT safe_body_json FROM engine_workflow_history WHERE run_id=$run AND kind='redo_requested'
                  AND generation=$generation AND json_extract(safe_body_json,'$.targetStepId')=$target ORDER BY history_seq
                """, r => JsonSerializer.Deserialize(r.Text(0), ReviewTransitionJson.Default.RedoRequest)!,
                new SqlArg("$run", target.Run), new("$generation", target.Generation), new("$target", c.EntityId));
            if (requests.Count == 0) return Refuse("redo_evidence_missing");
            // A gate settled itself when it was answered, and another worker's redo may already have reset it: only reviewers are waited for.
            var peers = requests.Where(request => request.SourceKind != GateRevisionSource).SelectMany(request => request.PeerStepIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var peerJson = JsonSerializer.Serialize(peers, EngineJson.Default.IReadOnlyListString);
            if (s.QueryFirst("""
                SELECT COUNT(*) FROM engine_step st WHERE st.id IN (SELECT value FROM json_each($peers))
                  AND st.generation=$generation AND st.state NOT IN ('succeeded','skipped','cancelled')
                  AND st.dispatch_state IS NOT 'review_settled'
                """, r => r.Long(0), new SqlArg("$peers", peerJson), new("$generation", target.Generation)) != 0) return Refuse("review_peers_pending");
            var affected = s.Query("""
                WITH RECURSIVE affected(id) AS (
                    SELECT $target UNION SELECT d.step_id FROM engine_step_dependency d JOIN affected a ON d.dependency_id=a.id
                ) SELECT st.id,st.generation,CASE WHEN a.generation<st.generation THEN 0 ELSE st.attempt END,COALESCE(a.execution_epoch,st.authority_epoch,0),st.kind
                  FROM engine_step st LEFT JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt
                  WHERE st.run_id=$run AND st.id IN affected ORDER BY st.id
                """, r => new WorkflowExecutionFence(r.Text(0), checked((int)r.Long(1)), checked((int)r.Long(2)), r.Long(3)),
                new SqlArg("$target", c.EntityId), new("$run", target.Run));
            // An attempt from an earlier generation was released when that generation advanced; nothing has run in this one.
            if (affected.Any(row => row.Attempt > 0 && !released(s, row))) return Refuse("executor_quiescence_required");
            if (s.QueryFirst("""
                SELECT COUNT(*) FROM engine_outbox o JOIN engine_step st ON st.id=json_extract(o.payload_json,'$.stepId') OR st.session_id=o.session_id
                WHERE st.run_id=$run AND (st.dispatch_state='redo_pending' OR st.id IN (SELECT value FROM json_each($peers)))
                  AND o.state NOT IN ('done','failed','dead')
                """, r => r.Long(0), new SqlArg("$run", target.Run), new("$peers", peerJson)) != 0) return Refuse("executor_effects_pending");
            return new(target, requests, affected);
        }
    }
}
