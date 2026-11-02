using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public PreparedForeignAncestryBatch PrepareForeignAncestry(ForeignAncestryArgs args) => new(_machine, args);

    public CommandResult ApplyForeignAncestryBatch(IWriteScope scope, PreparedForeignAncestryBatch prepared) =>
        EngineJournal.ApplyForeignAncestryBatch(scope, _machine, prepared);
}
