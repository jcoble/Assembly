using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Mcp;
using Assembly.Engine.Persistence;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
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

    public CommandResult ApplyLocalParentNeed(IWriteScope scope, McpCaller caller, PreparedParentNeed prepared, long nowMs)
    {
        var context = new CommandContext(prepared.OperationId, _machine, new(ActorKind.Session, caller.SessionId),
            prepared.Request.NeedId, prepared.Target.NeedVersion, prepared.Target.ChildEpoch, nowMs);
        return EngineJournal.Execute(scope, context, prepared.LocalCanonical(caller, _machine),
            () => NeedTransitions.ApplyLocalParent(scope, context, caller, prepared, _expiryPolicy));
    }

}
