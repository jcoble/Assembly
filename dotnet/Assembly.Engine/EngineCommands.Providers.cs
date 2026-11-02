using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Providers;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public static PreparedProviderCheck PrepareProviderCheck(RecordProviderCheck command) => new(command);

    public CommandResult ApplyProviderCheck(IWriteScope scope, CommandContext context, PreparedProviderCheck prepared) =>
        EngineJournal.Execute(scope, context, prepared.CanonicalJournal, () =>
        {
            if (context.Machine != _machine) return new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch);
            return ProviderCheckTransition.Apply(scope, context, prepared, _isConfiguredProvider);
        });
}
