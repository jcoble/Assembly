using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Messages;

public static partial class MessageTransitions
{
    internal static CommandResult ApplyRelaySend(IWriteScope scope, CommandContext context, SendMessage command,
        byte[] body, byte[] label, string senderKind, int messageLimit, MessageExpiryPolicy policy) =>
        Send(scope, context, command, Find(scope, context.EntityId), messageLimit, policy, body, label,
            senderKind, RelayEffectAssertion.Send(context.EntityId));

    internal static CommandResult ApplyRelayOutcome(IWriteScope scope, CommandContext context, MessageOutcome command,
        int messageLimit, MessageExpiryPolicy policy) =>
        ApplyCore(scope, context, command, messageLimit, policy, RelayEffectAssertion.Outcome(context.EntityId));
}
