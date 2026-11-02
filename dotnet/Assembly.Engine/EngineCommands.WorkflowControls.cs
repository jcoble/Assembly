using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public CommandResult OpenWorkflowGate(IWriteScope scope, CommandContext context)
    {
        if (context.Machine != _machine) return new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch);
        if (scope.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_command_receipt WHERE command_id=$id)", r => r.Long(0) != 0,
            new SqlArg("$id", context.CommandId.ToString("N"))))
            return EngineJournal.Execute(scope, context, "\"workflow_gate_open/v1\"", () => throw new InvalidOperationException("gate_receipt_missing"));
        return WorkflowGateTransitions.Open(scope, context);
    }

    public CommandResult ApplyWorkflowControl(IWriteScope scope, CommandContext context, WorkflowControl command) =>
        EngineJournal.Execute(scope, context, JsonSerializer.Serialize(command, WorkflowControlJson.Default.WorkflowControl), () =>
            context.Machine != _machine ? new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch)
                : WorkflowControlTransitions.Apply(scope, context, command));
}
