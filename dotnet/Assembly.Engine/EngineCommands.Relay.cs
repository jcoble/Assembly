using Assembly.Application.Common.Database;
using Assembly.Engine.Persistence;
using Assembly.Engine.Contracts;
using Assembly.Engine.Relays;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    internal RelaySendAck ReadRelayAck(IReadScope scope, RelaySourceDescriptor source) =>
        EngineJournal.ReadRelayAck(scope, _machine, source);

    internal RelaySendBeginReply ProbeRelaySend(IReadScope scope, RelaySendRequest request) =>
        EngineJournal.ProbeRelaySend(scope, _machine, request);

    internal RelaySendAck ApplyRelaySend(IWriteScope scope, RelaySendRequest request, PreparedRelaySend? prepared, long nowMs) =>
        EngineJournal.ApplyRelaySend(scope, _machine, request, prepared, _messageLimit, _expiryPolicy, nowMs);
    internal CommandResult ApplyRelayOutcome(IWriteScope scope, RelayOutcomeArgs args, long nowMs) =>
        EngineJournal.ApplyRelayOutcome(scope, _machine, args, _messageLimit, _expiryPolicy, nowMs);

}
