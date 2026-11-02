using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public CommandResult ApplyForeignAncestryBatch(IWriteScope scope, ForeignAncestryArgs args) =>
        EngineJournal.ApplyForeignAncestryBatch(scope, _machine, args);
}
