using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Messages;

public static partial class MessageTransitions
{
    internal static IReadOnlyList<EventDraft> WorkflowReportReminder(IWriteScope scope, CommandContext context,
        string messageId, string sessionId, string kind, IList<EffectDraft> effects, MessageExpiryPolicy policy)
    {
        if (Find(scope, messageId) is not null) throw new InvalidOperationException("Workflow report reminder identity conflict.");
        var message = new Message(messageId, 1, null, sessionId,
            $"This workflow step requires a typed {kind} receipt. Call report with state, summary, proofUrls and the receipt before ending the next turn.", "prompt", "queued");
        Insert(scope, context, message, "system", null, policy);
        scope.Execute("UPDATE engine_message SET held_epoch=$epoch,target_epoch=$epoch WHERE id=$id",
            new SqlArg("$epoch", context.Epoch), new("$id", messageId));
        List<EventDraft> events = [Changed(context, message)];
        ReleaseNext(scope, context, sessionId, effects, events);
        return events;
    }
}
