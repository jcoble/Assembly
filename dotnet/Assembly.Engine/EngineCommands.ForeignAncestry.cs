using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    // Existing private parent proofs certify structure only; metadata is read from these actual stored rows.
    internal IReadOnlyList<ForeignAncestor>? ReadKnownAncestry(IReadScope scope, string sessionId)
    {
        if (scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) != _machine)
            return null;
        var chain = SessionAncestry.Read(scope, _machine, sessionId);
        return chain.Known ? StoredTitleNodes(scope, chain.Nodes.Reverse().Select(n => n.Id).ToArray()) : null;
    }

    public PreparedForeignAncestryBatch PrepareForeignAncestry(ForeignAncestryArgs args) => new(_machine, args);

    public CommandResult ApplyForeignAncestryBatch(IWriteScope scope, PreparedForeignAncestryBatch prepared) =>
        EngineJournal.ApplyForeignAncestryBatch(scope, _machine, prepared);
}
