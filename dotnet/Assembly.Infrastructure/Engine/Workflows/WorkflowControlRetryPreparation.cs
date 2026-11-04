using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Workflows;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Infrastructure.Engine.Workflows;

internal static class WorkflowControlRetryPreparation
{
    internal static async Task<Func<IWriteScope, CommandResult>?> PrepareAsync(EngineRuntime runtime, TimeProvider time, WorkflowCommandRecovery? workflowRecovery, WindowCommand args, CommandOrigin origin, CancellationToken ct)
    {
        var step = args.Command switch { RetryWorkflowStep retry => retry.StepId, DecideWorkflowStep decide => decide.StepId, _ => null };
        var target = ((IEngineRuntime)runtime).Read(s => s.QueryFirst("""
            SELECT n.id,n.version FROM engine_needs_me n JOIN engine_step st ON st.id=n.step_id
            JOIN engine_run r ON r.id=st.run_id
            WHERE r.id=$run AND r.version=$version AND st.id=$step AND st.kind='command' AND st.state='waiting'
              AND st.outcome_reason='command_uncertain' AND n.source_kind='workflow' AND n.state='open'
              AND json_extract(n.execution_json,'$.reason')='command_uncertain'
            ORDER BY n.created_ms,n.id LIMIT 1
            """, r => new { Id = r.Text(0), Version = r.Long(1) }, new SqlArg("$run", args.EntityId), new("$version", args.ExpectedVersion), new("$step", step)), ct);
        if (target is null || workflowRecovery is null) return null;
        CommandContext Context() => new(args.CommandId, runtime.Machine, new(ActorKind.Window, "owner"), args.EntityId, args.ExpectedVersion, args.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds(), origin);
        try
        {
            var prepared = await workflowRecovery.PrepareAsync(Context() with { EntityId = target.Id, ExpectedVersion = target.Version }, ct);
            return scope => runtime.Commands.ApplyPreparedWorkflowControlRetry(scope, Context(), (WorkflowCommand)args.Command, prepared);
        }
        catch (InvalidOperationException error)
        {
            return scope => runtime.Commands.Apply(scope, Context(), args.Command, error.Message);
        }
    }
}
