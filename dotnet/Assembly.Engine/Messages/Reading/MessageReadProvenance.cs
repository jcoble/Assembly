using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Messages.Reading;

public static class MessageReadProvenance
{
    // Called only after the existing transition has authenticated this sender/relay context.
    public static string Origin(IWriteScope scope, CommandContext context, string? sender)
    {
        if (sender is not null)
            return scope.QueryFirst("SELECT machine FROM engine_session WHERE id=$id", row => row.Text(0), new SqlArg("$id", sender))
                ?? throw new InvalidOperationException("Message sender provenance is unavailable.");
        const string prefix = "machine:";
        return context.Actor.Kind == ActorKind.System && context.Actor.Id.StartsWith(prefix, StringComparison.Ordinal)
            ? context.Actor.Id[prefix.Length..] : context.Machine;
    }

    public static void OnMessageCreated(IWriteScope scope, string messageId, long eventSeq, string originMachine) =>
        scope.Execute("""
            UPDATE engine_message SET origin_machine=COALESCE(origin_machine,$origin),message_created_seq=$seq
            WHERE id=$id AND message_created_seq IS NULL
            """, new("$origin", originMachine), new("$seq", eventSeq), new("$id", messageId));
}
