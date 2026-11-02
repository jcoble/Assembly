using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;
using Assembly.Engine.Persistence;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public static bool IsAcceptedAuthTerminal(IReadScope scope, string sessionId, long terminalSeq, AuthTerminalProof proof)
    {
        var machine = scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0));
        return !string.IsNullOrWhiteSpace(machine) && EngineJournal.IsAcceptedAuthTerminal(scope, machine, sessionId, terminalSeq, proof);
    }
}
