namespace Assembly.Engine.Workflows.Runs;

/// <summary>What a stuck step can be moved with, for the row that offers it. The transition stays the authority: a worker
/// still stopping isn't part of this, since the row would otherwise lose its control just when the step is stuck, and the
/// transition refuses until it has stopped.</summary>
internal static class WorkflowStepRecovery
{
    private const string Live = "(SELECT state FROM engine_run WHERE id=t.run_id AND definition_hash IS NOT NULL) IN ('queued','running','needs_me')";
    private const string Retry = """
        (t.state='failed' AND t.attempt_in_generation<t.max_attempts
         OR t.state='waiting' AND t.outcome_reason IN ('session_lost','timed_out','interrupted','command_uncertain'))
        """;
    private const string Skip = "t.state='pending' AND COALESCE(t.kind,'')<>'gate' AND (t.required=0 OR t.rule_skippable=1 AND t.outcome_reason='condition_false')";
    private const string Continue = """
        t.state='waiting' AND t.outcome_reason='scope_required' AND t.scope_granted=1 AND t.session_id IS NULL
         AND NOT EXISTS(SELECT 1 FROM engine_needs_me n WHERE n.run_id=t.run_id AND n.step_id=t.id AND n.state='open')
        """;

    /// <summary>A step's <c>recovery</c> field, for an alias <c>t</c> of <c>engine_step</c>.</summary>
    internal const string Field = $"""
        'recovery',json((SELECT json_group_array(action) FROM (
          SELECT 'retry' action WHERE {Live} AND {Retry}
          UNION ALL SELECT 'skip' WHERE {Live} AND {Skip}
          UNION ALL SELECT 'continue' WHERE {Live} AND {Continue})))
        """;
}
