using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Messages;

public static partial class MessageTransitions
{
    internal static CommandResult TurnReply(IWriteScope scope, CommandContext context, string child, string parent,
        PreparedTurnReply prepared, MessageExpiryPolicy policy)
    {
        if (Find(scope, context.EntityId) is not null) throw new CallFailed("turn_reply_conflict");
        var target = FindRecipient(scope, parent);
        if (target is null) return Refused(context, "bad_args");
        var ended = target.State == "ended";
        var foreign = target.State == "foreign";
        var message = new Message(context.EntityId, 1, child, parent, prepared.Source.Text, "prompt", ended ? "failed" : "queued");
        Insert(scope, context, message, "session", ended ? "starter_ended" : null, policy, prepared.Body);
        List<EffectDraft> effects = []; List<EventDraft> events = [Changed(context, message)];
        if (!ended && !foreign) ReleaseNext(scope, context, parent, effects, events);
        return EngineJournal.Commit(scope, context, Find(scope, message.Id)!.Version, context.Epoch, events, effects);
    }
}
