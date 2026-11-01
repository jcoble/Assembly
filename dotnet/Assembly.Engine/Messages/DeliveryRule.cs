using Assembly.Application.Common.Database;

namespace Assembly.Engine.Messages;

public static class DeliveryRule
{
    public static string For(IWriteScope s, string? fromSessionId, string senderKind, string toSessionId)
    {
        if (senderKind is "window" or "system")
            return "steer";
        return s.QueryFirst("""
            SELECT CASE WHEN recipient.parent_id=sender.id OR sender.parent_id=recipient.id
                OR (sender.parent_id IS NOT NULL AND sender.parent_id=recipient.parent_id) THEN 'steer' ELSE 'wait' END
            FROM engine_session sender JOIN engine_session recipient ON recipient.id=$to WHERE sender.id=$from
            """, r => r.Text(0), new("$from", fromSessionId), new("$to", toSessionId)) ?? "wait";
    }
}
