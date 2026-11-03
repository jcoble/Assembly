using Assembly.Application.Common.Database;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Persistence;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    /// <summary>Exact owning release completion only; this supplies no native file/resume witness.</summary>
    public HandoffReleaseEffectData? ReadHandoffRelease(IReadScope scope, HandoffIdentity identity) =>
        EngineJournal.ReadHandoffRelease(scope, _machine, identity);

    public HandoffTargetFence ReadTargetHandoffFence(IReadScope scope, string sessionId) =>
        EngineJournal.ReadTargetHandoffFence(scope, _machine, sessionId);

    public HandoffAck ReadHandoffAck(IReadScope scope, HandoffIdentity identity) =>
        EngineJournal.ReadHandoffAck(scope, _machine, identity);

    public HandoffAck ApplyHandoffPhase(IWriteScope scope, HandoffPhaseArgs args, long nowMs) =>
        EngineJournal.ExecuteHandoffPhase(scope, _machine, args, nowMs);

    public HandoffAck ApplyHandoffAdopt(IWriteScope scope, HandoffAdoptArgs args, long nowMs) =>
        EngineJournal.ExecuteHandoffAdopt(scope, _machine, args, nowMs);

    public HandoffAck SealHandoffTarget(IWriteScope scope, HandoffSealArgs args, long nowMs) =>
        EngineJournal.ExecuteHandoffSeal(scope, _machine, args, nowMs);
}
