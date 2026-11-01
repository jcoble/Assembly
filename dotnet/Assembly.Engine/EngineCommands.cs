using Assembly.Engine.Effects;
using Assembly.Engine.Recording;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;
using Assembly.Engine.Runs;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Messages;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Prs;

namespace Assembly.Engine;

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
        // Raw boundary arguments are transient: even the journal fingerprint and size check use structural previews.
        var canonical = RecordingTransitions.JournalJson(command);
        return EngineJournal.Execute(scope, context, canonical, () =>
        {
            if (context.Machine != _machine)
                return new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch);
            return command switch
            {
                EffectCommand effect => EffectTransitions.Apply(scope, context, effect),
                RecordingCommand recording => RecordingTransitions.Apply(scope, context, recording),
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
