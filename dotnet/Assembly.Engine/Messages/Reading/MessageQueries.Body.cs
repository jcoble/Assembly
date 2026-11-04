using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Messages.Reading;

public sealed partial class MessageQueries
{
    public JsonElement Body(string messageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("bad_args");
        using var read = db.OpenSnapshot(ct);
        var bytes = read.QueryFirst("""
            SELECT length(CAST(m.text AS BLOB))+length(CAST(m.sender_label AS BLOB))+COALESCE(length(CAST(r.title AS BLOB)),0)
            FROM engine_message m LEFT JOIN engine_session r ON r.id=m.to_session_id WHERE m.id=$id AND m.machine=$machine
            """, row => (long?)row.Long(0), new SqlArg("$id", messageId), new("$machine", machine));
        if (bytes is null) throw new KeyNotFoundException("Message body is unavailable on this source.");
        if (bytes > 1024 * 1024) throw new InvalidOperationException("message_body_too_large");
        var body = read.QueryFirst("""
            SELECT m.origin_machine,m.id,m.version,m.text,COALESCE(sender.machine,m.origin_machine),m.from_session_id,m.sender_label,
                recipient.machine,m.to_session_id,COALESCE(recipient.title,m.to_session_id),run.machine,m.run_id,m.created_ms,
                m.sender_kind,m.state,m.delivery,m.reason,m.message_created_seq
            FROM engine_message m LEFT JOIN engine_session sender ON sender.id=m.from_session_id
              LEFT JOIN engine_session recipient ON recipient.id=m.to_session_id LEFT JOIN engine_run run ON run.id=m.run_id
            WHERE m.id=$id AND m.machine=$machine
            """, row =>
            {
                string? Text(int at) => row.IsNull(at) ? null : row.Text(at);
                return new MessageBody(row.IsNull(0) ? null : new(row.Text(0), row.Text(1)), new(machine, row.Text(1)), row.Long(2), row.Text(3),
                    Text(4), Text(5), row.Text(6), Text(7), row.Text(8), row.Text(9), Text(10), Text(11), row.Long(12), row.Text(13), row.Text(14),
                    row.Text(15), Text(16), row.IsNull(17) ? null : row.Long(17));
            }, new SqlArg("$id", messageId), new("$machine", machine))!;
        var encoded = JsonSerializer.SerializeToUtf8Bytes(body, MessageReadModelJson.Default.MessageBody);
        if (encoded.Length > 1024 * 1024 - 1024) throw new InvalidOperationException("message_body_too_large");
        using var json = JsonDocument.Parse(encoded); return json.RootElement.Clone();
    }

    public BodyMatch Match(MatchArgs args, CancellationToken ct)
    {
        if (args.Ids is null || args.Ids.Count > 200 || args.Ids.Any(string.IsNullOrWhiteSpace) || args.Search is null || args.Search.EnumerateRunes().Count() > 256)
            throw new ArgumentException("bad_args");
        var ids = JsonSerializer.Serialize(args.Ids.ToArray(), MessageReadModelJson.Default.StringArray);
        using var read = db.OpenSnapshot(ct);
        var result = new BodyMatch(read.Query("""
            SELECT m.id FROM engine_message m LEFT JOIN engine_session recipient ON recipient.id=m.to_session_id
            WHERE m.machine=$machine AND m.origin_machine=$machine AND m.id IN(SELECT value FROM json_each($ids))
              AND (instr(lower(m.text),lower($search))>0 OR instr(lower(m.sender_label),lower($search))>0
                   OR instr(lower(COALESCE(recipient.title,'')),lower($search))>0)
            ORDER BY m.id COLLATE BINARY LIMIT 200
            """, row => row.Text(0), new SqlArg("$machine", machine), new("$ids", ids), new("$search", args.Search)));
        if (JsonSerializer.SerializeToUtf8Bytes(result, MessageReadModelJson.Default.BodyMatch).Length > 1024 * 1024 - 1024)
            throw new InvalidOperationException("message_response_too_large");
        return result;
    }
}
