using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Persistence;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public static PreparedNeedCommand PrepareNeedCommand(NeedCommand command,
        CancellationToken cancellationToken = default) => PreparedNeedCommand.Prepare(command, cancellationToken);

    public CommandResult ApplyPreparedNeed(IWriteScope scope, CommandContext context, PreparedNeedCommand prepared) =>
        EngineJournal.Execute(scope, context, prepared.CanonicalJournal, () => context.Machine != _machine
            ? new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch)
            : NeedTransitions.Apply(scope, context, prepared.Command, _expiryPolicy));
}
