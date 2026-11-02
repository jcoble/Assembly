using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows.Execution;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public CommandResult ApplyPreparedCommandStep(IWriteScope scope, CommandContext context, PreparedCommandStep prepared) =>
        EngineJournal.Execute(scope, context, prepared.CanonicalJournal, () => context.Machine != _machine
            ? new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch)
            : CommandStepTransitions.Start(scope, context, prepared));

    public CommandResult OpenWorkflowExecutionNeed(IWriteScope scope, CommandContext context, Effect effect, string reason)
    {
        var canonical = "{\"effectId\":" + JsonSerializer.Serialize(effect.Id, EngineJson.Default.String)
            + ",\"reason\":" + JsonSerializer.Serialize(reason, EngineJson.Default.String) + "}";
        return EngineJournal.Execute(scope, context, canonical, () => context.Machine != _machine
            ? new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch)
            : WorkflowExecutionNeeds.Open(scope, context, effect, reason));
    }

    public CommandResult ObserveWorkflowProcess(IWriteScope scope, CommandContext context, CommandProcessObserved observed) =>
        EngineJournal.Execute(scope, context, JsonSerializer.Serialize(observed, WorkflowExecutionJson.Default.CommandProcessObserved), () =>
            context.Machine != _machine ? new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch)
                : CommandStepTransitions.Observe(scope, context, observed));
}
