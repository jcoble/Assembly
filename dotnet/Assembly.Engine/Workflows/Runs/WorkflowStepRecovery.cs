using Assembly.Application.Common.Engine;

namespace Assembly.Engine.Workflows.Runs;

/// <summary>What a stuck step can be moved with, for the row that offers it. The transition stays the authority: a worker
/// still stopping isn't part of this, since the row would otherwise lose its control just when the step is stuck, and the
/// transition refuses until it has stopped.</summary>
internal static class WorkflowStepRecovery
{
    /// <summary>Skip is for getting past something. An optional step that's simply next isn't offered it, only one that can't
    /// start because a step before it is stuck, or one a rule has already ruled out. A gate is only ever answered.</summary>
    private const string Skip = $"""
        t.state='pending' AND COALESCE(t.kind,'')<>'gate' AND (t.rule_skippable=1 AND t.outcome_reason='condition_false'
         OR t.required=0 AND EXISTS(SELECT 1 FROM engine_step_dependency d JOIN engine_step b ON b.id=d.dependency_id
           WHERE d.step_id=t.id AND (b.state='failed' OR b.state='waiting' AND b.{OwnerAttention.Lost})))
        """;
    /// <summary>A step's <c>recovery</c> field, for an alias <c>t</c> of <c>engine_step</c>.</summary>
    internal const string Field = $"""
        'recovery',json((SELECT json_group_array(action) FROM (
          SELECT 'retry' action WHERE {OwnerAttention.Live} AND {OwnerAttention.Retry}
          UNION ALL SELECT 'skip' WHERE {OwnerAttention.Live} AND {Skip}
          UNION ALL SELECT 'continue' WHERE {OwnerAttention.Live} AND {OwnerAttention.Continue})))
        """;
}
