using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Messages;

public sealed record MessageExpiryBatch(IReadOnlyList<EventDraft> Events, IReadOnlyList<string> Recipients, bool More);

/// <summary>Pure, bounded expiry transitions within the caller's owning writer.</summary>
public static class MessageExpiry
{
    // Payload identity protects unresolved historical attempts as well as legacy send/{M} IDs.
    internal static readonly string ReservationFree = $"""
        NOT EXISTS (
          SELECT 1 FROM engine_outbox o WHERE o.kind='send'
            AND (json_extract(o.payload_json,'$.messageId')=m.id
              OR o.id='send/'||m.id OR o.id='send/'||m.id||'/'||m.attempt)
            AND (o.state NOT IN ('done','failed','dead')
              OR json_extract(o.receipt_json,'$.disposition')={(int)EffectDisposition.Unknown}))
        """;

    internal static readonly string Eligible = $"""
        m.machine=$machine AND se.machine=$machine AND se.holder='local'
        AND se.state NOT IN ('foreign','handing_off')
        AND m.state IN ('queued','held') AND m.in_flight=0 AND m.expires_ms IS NOT NULL
        AND {ReservationFree}
        """;

    public static long? NextDeadline(IReadScope scope, string machine) => scope.QueryFirst($"""
        SELECT MIN(m.expires_ms) FROM engine_message m JOIN engine_session se ON se.id=m.to_session_id
        WHERE {Eligible}
        """, r => r.IsNull(0) ? (long?)null : r.Long(0), new SqlArg("$machine", machine));

    public static MessageExpiryBatch ExpireDue(IWriteScope scope, CommandContext context, string? recipientId = null, int limit = 100)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var rows = scope.Query($"""
            SELECT m.id,m.version,m.attempt,m.to_session_id,m.from_session_id,se.epoch
            FROM engine_message m JOIN engine_session se ON se.id=m.to_session_id
            WHERE {Eligible} AND m.expires_ms<=$now AND ($recipient IS NULL OR m.to_session_id=$recipient)
            ORDER BY m.expires_ms,m.rowid LIMIT $limit
            """, r => new
        {
            Id = r.Text(0),
            Version = r.Long(1),
            Attempt = r.Long(2),
            To = r.Text(3),
            From = r.IsNull(4) ? null : r.Text(4),
            Epoch = r.Long(5)
        },
            new("$machine", context.Machine), new("$now", context.NowMs), new("$recipient", recipientId), new("$limit", limit));
        List<EventDraft> events = [];
        List<string> recipients = [];
        foreach (var row in rows)
        {
            var updated = scope.Execute($"""
                UPDATE engine_message AS m SET state='failed',reason='expired',in_flight=0,version=version+1,updated_ms=$now
                WHERE m.id=$id AND m.version=$version AND m.attempt=$attempt AND m.expires_ms<=$now
                  AND EXISTS(SELECT 1 FROM engine_session se WHERE se.id=m.to_session_id AND se.epoch=$epoch AND {Eligible})
                """, new("$machine", context.Machine), new("$now", context.NowMs), new("$id", row.Id),
                new("$version", row.Version), new("$attempt", row.Attempt), new("$epoch", row.Epoch));
            if (updated == 0) continue;
            events.Add(new("message.changed", "message", row.Id, row.Version + 1, false,
                new(row.To, null, null, row.From, "failed", row.Epoch)));
            if (!recipients.Contains(row.To, StringComparer.Ordinal)) recipients.Add(row.To);
        }
        var more = scope.QueryFirst($"""
            SELECT EXISTS(SELECT 1 FROM engine_message m JOIN engine_session se ON se.id=m.to_session_id
              WHERE {Eligible} AND m.expires_ms<=$now AND ($recipient IS NULL OR m.to_session_id=$recipient))
            """, r => r.Long(0) != 0, new("$machine", context.Machine), new("$now", context.NowMs), new("$recipient", recipientId));
        return new(events, recipients, more);
    }
}
