using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows;
using Assembly.Engine.Workflows.Runs;
using Assembly.Engine.Workflows.Workspaces;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public CommandResult ApplyPreparedWorkflow(IWriteScope scope, CommandContext context, PreparedWorkflowCommand prepared)
    {
        var result = EngineJournal.Execute(scope, context, prepared.CanonicalJournal, () =>
        {
            if (prepared.Workspace is { } workspace)
                return context.Machine != _machine ? new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch)
                    : WorkspaceTransitions.Apply(scope, context, workspace);
            if (context.Machine != _machine || prepared.Start.Context.Machine != _machine)
                return new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch);
            if (prepared.Start.Context != context with { NowMs = 0 })
                return new(false, false, "bad_args", context.ExpectedVersion, 0, context.Epoch);
            return WorkflowRunTransitions.Start(scope, context, prepared);
        });
        if (prepared.Workspace is null && result.Applied && WorkflowStarts.Read(scope, prepared.Start.Request) is null)
        {
            var start = prepared.Start;
            scope.Execute("""
                INSERT INTO engine_workflow_start(owner_id,request_key,request_fingerprint,run_id,original_command_id)
                VALUES($owner,$key,$fingerprint,$run,$command)
                """, new("$owner", start.Request.OwnerSessionId), new("$key", start.Request.RequestKey),
                new("$fingerprint", start.RequestFingerprint), new("$run", start.Request.ProposedRunId), new("$command", context.CommandId.ToString("N")));
        }
        return result;
    }
}
