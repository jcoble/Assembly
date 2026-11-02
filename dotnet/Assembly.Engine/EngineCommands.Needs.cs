using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Mcp;
using Assembly.Engine.Persistence;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public PreparedParentNeedIntent PrepareParentNeedIntent(Guid operationId, McpCaller caller,
        ParentNeedRequest input, CancellationToken ct) =>
        PreparedParentNeedIntent.Prepare(_machine, operationId, caller, input, ct);

    public ParentNeedIntentAccepted BeginParentNeedIntent(IWriteScope scope, McpCaller caller,
        PreparedParentNeedIntent prepared, long nowMs)
    {
        if (prepared.Payload.Source.ParentSessionId != caller.SessionId || prepared.Payload.Source.ParentEpoch != caller.Epoch)
            throw new ArgumentException("The prepared source caller does not match.", nameof(caller));
        return EngineJournal.BeginParentNeedIntent(scope, _machine, prepared, nowMs);
    }

    internal CommandResult PrepareParentNeedIntent(IWriteScope scope, PreparedParentNeedPhase prepared, long nowMs) =>
        ApplyParentNeedPhase(scope, prepared, ParentNeedPhaseWrite.Prepare, nowMs);

    internal CommandResult DispatchParentNeedIntent(IWriteScope scope, PreparedParentNeedPhase prepared, long nowMs) =>
        ApplyParentNeedPhase(scope, prepared, ParentNeedPhaseWrite.Dispatch, nowMs);

    internal CommandResult CompleteParentNeedIntent(IWriteScope scope, PreparedParentNeedPhase prepared, long nowMs) =>
        ApplyParentNeedPhase(scope, prepared, ParentNeedPhaseWrite.Outcome, nowMs);

    private CommandResult ApplyParentNeedPhase(IWriteScope scope, PreparedParentNeedPhase prepared, ParentNeedPhaseWrite expected, long nowMs)
    {
        if (prepared.Write != expected) throw new ArgumentException("The prepared source phase does not match.", nameof(prepared));
        return EngineJournal.ApplyParentNeedPhase(scope, _machine, prepared, nowMs);
    }

    public static PreparedNeedCommand PrepareNeedCommand(NeedCommand command,
        CancellationToken cancellationToken = default) => PreparedNeedCommand.Prepare(command, cancellationToken);

    public CommandResult ApplyPreparedNeed(IWriteScope scope, CommandContext context, PreparedNeedCommand prepared) =>
        EngineJournal.Execute(scope, context, prepared.CanonicalJournal, () => context.Machine != _machine
            ? new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch)
            : NeedTransitions.Apply(scope, context, prepared.Command, _expiryPolicy));
    public static PreparedNeedCommand PrepareOwnerNeedCommand(Guid originalCommandId, string owningMachine, string needId,
        long originalExpectedVersion, long originalEpoch, ResolveNeed originalCommand, PreparedProviderAnswer? captured,
        string? preparationError, CancellationToken cancellationToken) =>
        PreparedNeedCommand.PrepareOwner(originalCommandId, owningMachine, needId, originalExpectedVersion, originalEpoch,
            originalCommand, captured, preparationError, cancellationToken);

    public CommandResult ApplyPreparedOwnerNeed(IWriteScope scope, PreparedNeedCommand prepared, long nowMs) =>
        EngineJournal.ExecuteOwnerNeed(scope, prepared, nowMs, _expiryPolicy);

    public static PreparedParentNeed PrepareParentNeed(Guid operationId, ParentNeedRequest request, NeedAnswerTarget target,
        ProviderAnswerBinding? providerBinding, CancellationToken cancellationToken) =>
        PreparedParentNeed.Prepare(operationId, request, target, providerBinding, cancellationToken);

    public static PreparedForwardedParentNeed PrepareForwardedParentNeed(ParentNeedSource source,
        NeedAnswerTarget target, PreparedParentNeed prepared) => PreparedForwardedParentNeed.Prepare(source, target, prepared);

    public NeedAnswerAck ReadForwardedParentNeed(IReadScope scope, NeedAnswerRequestDescriptor descriptor) =>
        EngineJournal.ReadForwardedParentNeed(scope, _machine, descriptor);

    public CommandResult ApplyForwardedParentNeed(IWriteScope scope, PreparedForwardedParentNeed prepared, long nowMs) =>
        EngineJournal.ApplyForwardedParentNeed(scope, _machine, prepared, nowMs, _expiryPolicy);

    public CommandResult ApplyLocalParentNeed(IWriteScope scope, McpCaller caller, PreparedParentNeed prepared, long nowMs)
    {
        var context = new CommandContext(prepared.OperationId, _machine, new(ActorKind.Session, caller.SessionId),
            prepared.Request.NeedId, prepared.Target.NeedVersion, prepared.Target.ChildEpoch, nowMs);
        return EngineJournal.Execute(scope, context, prepared.LocalCanonical(caller, _machine),
            () => NeedTransitions.ApplyLocalParent(scope, context, caller, prepared, _expiryPolicy));
    }

}
