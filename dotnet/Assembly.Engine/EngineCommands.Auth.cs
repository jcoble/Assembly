using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;
using Assembly.Engine.Persistence;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public bool IsAcceptedAuthTerminal(IReadScope scope, string sessionId, long terminalSeq, AuthTerminalProof proof) =>
        EngineJournal.IsAcceptedAuthTerminal(scope, _machine, sessionId, terminalSeq, proof);
}
