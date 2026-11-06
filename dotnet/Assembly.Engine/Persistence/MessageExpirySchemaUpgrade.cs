using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Messages;

namespace Assembly.Engine.Persistence;

internal static class MessageExpirySchemaUpgrade
{
    internal const string Index = """
        CREATE INDEX engine_message_pending_expiry ON engine_message(machine,expires_ms)
        WHERE state IN ('queued','held') AND expires_ms IS NOT NULL;
        """;

    internal static void Apply(IWriteScope scope, int minutes)
    {
        if (minutes <= 0) throw new ArgumentOutOfRangeException(nameof(minutes));
        scope.Execute("ALTER TABLE engine_message ADD COLUMN expires_ms INTEGER NULL");
        long? after = null;
        while (true)
        {
            var rows = scope.Query("""
                SELECT rowid,typeof(created_ms),CASE WHEN typeof(created_ms)='integer' THEN created_ms ELSE 0 END
                FROM engine_message WHERE state IN ('queued','held') AND ($after IS NULL OR rowid>$after)
                ORDER BY rowid LIMIT 100
                """, r => new { RowId = r.Long(0), Type = r.Text(1), CreatedMs = r.Long(2) }, new SqlArg("$after", after));
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                if (row.Type != "integer" || row.CreatedMs < -62_135_596_800_000L || row.CreatedMs > 253_402_300_799_999L)
                    throw new CallFailed("message_expiry_overflow");
                var deadline = MessageExpiryPolicy.Calculate(row.CreatedMs, minutes);
                scope.Execute("UPDATE engine_message SET expires_ms=$deadline WHERE rowid=$rowid",
                    new("$deadline", deadline), new("$rowid", row.RowId));
            }
            after = rows[^1].RowId;
        }
        scope.Execute(Index);
        scope.Execute("UPDATE engine_meta SET schema_version=6 WHERE singleton=1");
    }
}
