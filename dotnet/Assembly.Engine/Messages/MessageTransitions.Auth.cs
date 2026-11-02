using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Messages;

public static partial class MessageTransitions
{
    internal static EventDraft ApplyInsertedAuthTerminal(IWriteScope scope, CommandContext context, AuthExecution execution)
    {
        var message = Find(scope, execution.Delivery.Intent.MessageId);
        if (message is null || execution.ConfirmedProviderId is not { } providerId || string.IsNullOrWhiteSpace(providerId) ||
            scope.Execute("""
                UPDATE engine_message SET state='failed',reason='inserted_before_sign_in',version=version+1,updated_ms=$now
                WHERE id=$id AND version=$version AND state='next_turn' AND in_flight=0 AND provider_turn_id=$provider
                """, new("$now", context.NowMs), new("$id", message.Id), new("$version", message.Version), new("$provider", providerId)) != 1)
            throw new CallFailed("bad_receipt");
        return Changed(context, message with { Version = message.Version + 1, State = "failed" });
    }
}
