using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Sessions;
using Assembly.Application.Features.Engine.Runs;
using Assembly.Application.Features.Engine.NeedsMe;
using Assembly.Application.Features.Engine.Messages;
using Assembly.Application.Features.Engine.Handoffs;
using Assembly.Application.Features.Engine.Prs;

namespace Assembly.Application.Features.Engine;

public sealed class EngineCommands
{
    private readonly string _machine;
    private readonly Func<int> _childLimit;

    public EngineCommands(string machine, Func<int> childLimit)
    {
        _machine = machine;
        ArgumentNullException.ThrowIfNull(childLimit);
        _childLimit = childLimit;
    }

    public CommandResult Apply(IWriteScope scope, CommandContext context, EngineCommand command)
    {
        var canonical = JsonSerializer.Serialize(command, EngineJson.Default.EngineCommand);
        return EngineJournal.Execute(scope, context, canonical, () =>
        {
            if (context.Machine != _machine)
                return new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch);
            return command switch
            {
                PrCommand pr => PrTransitions.Apply(scope, context, pr),
                HandoffCommand handoff => HandoffTransitions.Apply(scope, context, handoff),
                MessageCommand message => MessageTransitions.Apply(scope, context, message),
                NeedCommand need => NeedTransitions.Apply(scope, context, need),
                RunCommand run => RunTransitions.Apply(scope, context, run),
                StepCommand step => StepTransitions.Apply(scope, context, step),
                SessionCommand session => SessionTransitions.Apply(scope, context, session, _childLimit()),
                _ => new(false, false, "unsupported_command", context.ExpectedVersion, 0, context.Epoch)
            };
        });
    }
}
