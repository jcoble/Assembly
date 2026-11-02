using Assembly.Application.Common.Database;

namespace Assembly.Engine.Workflows.Scheduling;

/// <summary>SQL eligibility; reservation and admission happen later in the supplied writer.</summary>
public static class WorkflowSchedule
{
    internal const string UnsettledDependency = """
        SELECT 1 FROM engine_step_dependency d JOIN engine_step dependency ON dependency.id=d.dependency_id
        WHERE d.step_id=st.id AND NOT (
          dependency.state='succeeded'
          OR (dependency.state='skipped' AND dependency.rule_skippable=1 AND dependency.outcome_reason IS NOT NULL)
          OR (d.optional=1 AND dependency.state IN ('failed','cancelled')))
        """;

    public static IReadOnlyList<string> Ready(IReadScope scope, string machine, long nowMs, int limit)
    {
        _ = nowMs;
        return scope.Query($"""
            SELECT st.id FROM engine_step st JOIN engine_run run ON run.id=st.run_id
            JOIN engine_session owner ON owner.id=run.owner_session_id
            JOIN engine_workflow_workspace workspace ON workspace.id=st.workspace_id
            WHERE run.machine=$machine AND run.definition_hash IS NOT NULL
              AND run.state IN ('queued','running','needs_me') AND run.paused=0 AND run.setup_state='ready'
              AND owner.machine=$machine AND owner.holder='local' AND owner.parent_id IS NULL
              AND owner.state IN ('idle','running','needs_me') AND owner.cancel_requested=0 AND owner.reserved_epoch IS NULL
              AND st.state='pending' AND COALESCE(st.dispatch_state,'')<>'redo_pending' AND st.kind IN ('agent','review')
              AND (st.session_id IS NULL OR st.kind='agent' AND st.attempt_in_generation=0 AND EXISTS(
                SELECT 1 FROM engine_step_attempt original JOIN engine_session worker ON worker.id=st.session_id
                WHERE original.step_id=st.id AND original.attempt=st.attempt AND original.generation<st.generation
                  AND worker.state='ended' AND worker.provider_session_id IS NOT NULL AND worker.provider_session_id<>''))
              AND workspace.run_id=run.id AND workspace.step_id=st.id AND workspace.state='ready'
              AND workspace.path IS NOT NULL AND workspace.head IS NOT NULL
              AND NOT EXISTS ({UnsettledDependency})
              AND (SELECT COUNT(*) FROM engine_step held JOIN engine_run active ON active.id=held.run_id
                   WHERE active.machine=$machine AND active.definition_hash IS NOT NULL
                     AND held.kind IN ('agent','review') AND held.session_id IS NOT NULL
                     AND (held.state IN ('running','waiting') OR EXISTS (
                       SELECT 1 FROM engine_session worker WHERE worker.id=held.session_id AND worker.state<>'ended')))
                  < (SELECT MIN(max_sessions) FROM engine_run participating
                     WHERE participating.machine=$machine AND participating.definition_hash IS NOT NULL
                       AND participating.state IN ('queued','running','needs_me','cancelling'))
            ORDER BY run.created_ms,run.id,st.rowid LIMIT $limit
            """, r => r.Text(0), new SqlArg("$machine", machine), new("$limit", Math.Clamp(limit, 0, 100)));
    }
}
