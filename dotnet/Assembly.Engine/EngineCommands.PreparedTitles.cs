using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public static PreparedSessionTitleCommand PrepareSessionTitle(SessionCommand command) => PreparedSessionTitleCommand.Prepare(command);

    public CommandResult ApplyPreparedSessionTitle(IWriteScope scope, CommandContext context, PreparedSessionTitleCommand prepared) => ApplyPreparedSessionTitle(scope, context, prepared, null);

    internal CommandResult ApplyPreparedSessionTitle(IWriteScope scope, CommandContext context, PreparedSessionTitleCommand prepared, string? preparedError) =>
        EngineJournal.Execute(scope, context, prepared.CanonicalJournal, () => context.Machine != _machine
            ? new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch)
            : preparedError is not null ? new(false, false, preparedError, context.ExpectedVersion, 0, context.Epoch)
            : ApplySession(scope, context, prepared.Command, prepared.TitleUtf8, prepared.Command is StartSession
                ? prepared.StartEffectPayload ?? throw new InvalidOperationException("Prepared Start payload missing.") : null));

    public CommandResult ApplyPreparedHelperPrompt(IWriteScope scope, CommandContext context, PreparedHelperPrompt prepared) =>
        EngineJournal.Execute(scope, context, prepared.CanonicalJournal, () =>
        {
            if (context.Machine != _machine)
                return new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch);
            if (context.Actor.Kind != ActorKind.Session || context.Actor.Id != prepared.Command.FromSessionId)
                return new(false, false, "forbidden", context.ExpectedVersion, 0, context.Epoch);
            if (prepared.Oversized)
            {
                var caller = scope.QueryFirst("SELECT epoch,CAST(title AS BLOB) FROM engine_session WHERE id=$id AND machine=$machine AND holder='local' AND state NOT IN ('foreign','ended')",
                    r => (Epoch: r.Long(0), Title: r.Blob(1)), new("$id", context.Actor.Id), new("$machine", _machine));
                if (caller.Title is null || caller.Epoch != prepared.CallerEpoch || !caller.Title.AsSpan().SequenceEqual(prepared.CallerTitleUtf8))
                    return new(false, false, "stale_caller", context.ExpectedVersion, 0, context.Epoch);
            }
            return MessageTransitions.ApplyPreparedHelper(scope, context, prepared.Command, prepared.BodyUtf8, _messageLimit(), _expiryPolicy);
        });
}
