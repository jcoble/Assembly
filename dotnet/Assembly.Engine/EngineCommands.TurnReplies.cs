using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public static PreparedTurnReply PrepareTurnReply(SessionObservation observed, SessionTurnReply final,
        FinalizedChatReply source, CancellationToken cancellationToken = default) => PreparedTurnReply.Live(observed, final, source, cancellationToken);
    public static PreparedTurnReply PrepareRecoveredTurnReply(RecoveredChatTurn recovered, FinalizedChatReply source,
        string currentOwningMachine, long recoveryEpoch) => PreparedTurnReply.Recovery(recovered, source, currentOwningMachine, recoveryEpoch);
    public CommandResult ApplyTurnReply(IWriteScope scope, PreparedTurnReply reply, long nowMs) =>
        EngineJournal.ApplyTurnReply(scope, _machine, reply, nowMs, _expiryPolicy, recovery: false);
    public CommandResult ApplyRecoveredTurnReply(IWriteScope scope, PreparedTurnReply reply, long nowMs) =>
        EngineJournal.ApplyTurnReply(scope, _machine, reply, nowMs, _expiryPolicy, recovery: true);
}
