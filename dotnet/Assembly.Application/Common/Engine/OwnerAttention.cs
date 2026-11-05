namespace Assembly.Application.Common.Engine;

/// <summary>The one definition of what needs the person, in SQL so each read counts the same rows. The engine sends the
/// answers on its rows as <c>forOwner</c> and <c>needsOwner</c>; clients draw those and work nothing out themselves.</summary>
public static class OwnerAttention
{
    /// <summary>A question the person answers, for an alias <c>n</c> of <c>engine_needs_me</c>. One addressed to a session is
    /// that session's, except a workflow's own question to the session that owns its run: that one has only the person to answer it.</summary>
    public const string Need = """
        (n.addressee_session_id IS NULL OR (n.source_kind IN ('workflow','workflow_gate') AND n.addressee_session_id=n.session_id
          AND EXISTS(SELECT 1 FROM engine_run run WHERE run.id=n.run_id AND run.owner_session_id=n.session_id)))
        """;

    public const string Live = "(SELECT state FROM engine_run WHERE id=t.run_id AND definition_hash IS NOT NULL) IN ('queued','running','needs_me')";
    public const string Lost = "outcome_reason IN ('session_lost','timed_out','interrupted','command_uncertain')";
    public const string Retry = $"""
        (t.state='failed' AND t.attempt_in_generation<t.max_attempts
         OR t.state='waiting' AND t.{Lost})
        """;
    public const string Continue = """
        t.state='waiting' AND t.outcome_reason='scope_required' AND t.scope_granted=1 AND t.session_id IS NULL
         AND NOT EXISTS(SELECT 1 FROM engine_needs_me n WHERE n.run_id=t.run_id AND n.step_id=t.id AND n.state='open')
        """;

    /// <summary>A step stopped until the person moves it, for an alias <c>t</c> of <c>engine_step</c>. A step with a question
    /// open is counted by that question, so nothing counts twice.</summary>
    public const string Step = $"""
        ({Live} AND ({Retry} OR {Continue})
         AND NOT EXISTS(SELECT 1 FROM engine_needs_me n WHERE n.run_id=t.run_id AND n.step_id=t.id AND n.state='open' AND {Need}))
        """;
}
