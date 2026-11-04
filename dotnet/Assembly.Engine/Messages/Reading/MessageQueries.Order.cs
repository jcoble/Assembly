using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Messages.Reading;

public sealed partial class MessageQueries
{
    private string NameEligible(MessageScope scope) => MessageMetadata.Cte + $"""
        , eligible AS (
          SELECT c.origin_machine,c.id,c.origin_created_ms,c.recipient_machine,c.to_session_id,c.recipient_version,
            {MessageMetadata.Bucket(scope.Sort)} AS bucket,{MessageMetadata.Name(scope.Sort)} AS full_name,
            COALESCE(c.{(scope.Sort == "sender" ? "sender" : "receiver")}_full_available,0) AS full_available
          FROM canonical c WHERE {MessageMetadata.Scope}
        )
        """;

    private const string DirectTail = """
        , anchor_row AS (
          SELECT origin_machine,id,origin_created_ms,bucket,full_name FROM eligible
          WHERE origin_machine=$afterOrigin AND id=$afterId AND origin_created_ms=$afterTime
        ), remaining AS (
          SELECT origin_machine,id,origin_created_ms,bucket,full_name,full_available,recipient_machine,to_session_id
          FROM eligible e WHERE ($afterId IS NULL OR EXISTS(SELECT 1 FROM anchor_row a WHERE e.bucket COLLATE NOCASE>=a.bucket COLLATE NOCASE))
            AND ($completed IS NULL OR e.bucket COLLATE NOCASE>$completed COLLATE NOCASE)
        ), buckets AS (
          SELECT min(bucket COLLATE BINARY) AS bucket,min(full_available) AS all_full,count(*) AS logical_count
          FROM remaining GROUP BY bucket COLLATE NOCASE
        ), authorities AS (
          SELECT bucket,recipient_machine,to_session_id FROM remaining GROUP BY bucket COLLATE NOCASE,recipient_machine,to_session_id
        ), modes AS (
          SELECT b.bucket,b.all_full,CASE WHEN b.all_full=1 OR ($sort='sender' AND b.logical_count=1)
            OR ($sort='receiver' AND (SELECT count(*) FROM authorities a WHERE a.bucket COLLATE NOCASE=b.bucket COLLATE NOCASE)=1)
            THEN 1 ELSE 0 END AS direct FROM buckets b
        ), blocked AS (
          SELECT bucket FROM modes WHERE direct=0 ORDER BY bucket COLLATE NOCASE,bucket COLLATE BINARY LIMIT 1
        ), anchor AS (
          SELECT a.origin_machine,a.id,a.origin_created_ms,a.bucket,CASE WHEN m.all_full=1 THEN a.full_name ELSE a.bucket END AS ordering_name
          FROM anchor_row a JOIN modes m ON m.bucket COLLATE NOCASE=a.bucket COLLATE NOCASE WHERE m.direct=1
        ), direct_rows AS (
          SELECT e.origin_machine,e.id,e.origin_created_ms,CASE WHEN m.all_full=1 THEN e.full_name ELSE e.bucket END AS ordering_name
          FROM remaining e JOIN modes m ON m.bucket COLLATE NOCASE=e.bucket COLLATE NOCASE
          WHERE m.direct=1 AND (NOT EXISTS(SELECT 1 FROM blocked) OR e.bucket COLLATE NOCASE<(SELECT bucket FROM blocked) COLLATE NOCASE)
        )
        """;

    public MessageDirectPage DirectNamePage(MessageScope scope, MessageCursor? cursor, int limit, CancellationToken ct)
    {
        Validate(scope, limit); if (scope.Sort == "time") throw new ArgumentException("bad_args");
        using var read = db.OpenSnapshot(ct); var sources = MessageCursors.Sources(read, machine);
        cursor ??= new(MessageCursors.Hash(scope, sources), sources.Select(s => new MessageCut(s.Machine, s.High, s.At, s.Generation)).ToArray(), null);
        MessageCursors.Validate(read, cursor, MessageCursors.Hash(scope, sources), sources);
        if (cursor.Name is { Completed: false } active) return new(NamePage(read, scope, cursor, [], true, sources), active.Bucket);
        if (cursor.Name is { Completed: true } && cursor.After is not null) throw new ArgumentException("bad_args");
        var args = Arguments(scope, cursor.After).Concat([new SqlArg("$completed", cursor.Name?.Bucket), new("$sort", scope.Sort), new("$limit", limit + 1)]).ToArray();
        var sql = NameEligible(scope) + DirectTail;
        if (cursor.After is not null && read.QueryFirst(sql + " SELECT count(*) FROM anchor", r => r.Long(0), args) != 1)
            throw new InvalidOperationException("query_changed");
        var blocked = read.QueryFirst(sql + " SELECT bucket FROM blocked", r => r.Text(0), args);
        var rows = read.Query(sql + $"""
            SELECT {MessageMetadata.ItemColumns} FROM direct_rows d JOIN canonical c ON c.origin_machine=d.origin_machine AND c.id=d.id
            WHERE $afterId IS NULL OR EXISTS(SELECT 1 FROM anchor a WHERE
              d.ordering_name COLLATE NOCASE>a.ordering_name COLLATE NOCASE OR
              (d.ordering_name COLLATE NOCASE=a.ordering_name COLLATE NOCASE AND
                (d.origin_created_ms<a.origin_created_ms OR d.origin_created_ms=a.origin_created_ms AND
                  (d.origin_machine COLLATE BINARY>a.origin_machine COLLATE BINARY OR d.origin_machine=a.origin_machine AND d.id COLLATE BINARY>a.id COLLATE BINARY))))
            ORDER BY d.ordering_name COLLATE NOCASE,d.origin_created_ms DESC,d.origin_machine COLLATE BINARY,d.id COLLATE BINARY LIMIT $limit
            """, r => Read(r), args);
        var more = rows.Count > limit; if (more) rows.RemoveAt(limit);
        var captured = rows.Count > 0 ? cursor with { After = rows[^1].Order, Name = null } : cursor;
        if (rows.Count == 0 && blocked is not null) captured = cursor with { After = null, Name = new(blocked, []) };
        return new(NamePage(read, scope, captured, rows.Select(r => r.Item).ToArray(), more || blocked is not null, sources), blocked);
    }

    private MessagePage NamePage(IReadSnapshot read, MessageScope scope, MessageCursor cursor, IReadOnlyList<MessageDisplay> items, bool more, IReadOnlyList<MessageSource> sources)
    {
        var page = new MessagePage(items, more ? MessageCursors.Encode(cursor) : null, cursor.Cuts, Missing(read, scope, sources), Unknown(read, scope)) { Captured = cursor }; Budget(page); return page;
    }

    public IReadOnlyList<MessageReceiverAuthority> ReceiverRefs(MessageScope scope, MessageCursor cursor, string bucket, CancellationToken ct)
    {
        using var read = db.OpenSnapshot(ct); var args = Arguments(scope, null).Append(new SqlArg("$bucket", bucket)).ToArray();
        var refs = read.Query(NameEligible(scope) + """
            SELECT DISTINCT recipient_machine,to_session_id,recipient_version FROM eligible
            WHERE bucket COLLATE NOCASE=$bucket COLLATE NOCASE AND full_available=0
            ORDER BY recipient_machine,to_session_id LIMIT 801
            """, r => new MessageReceiverAuthority(r.Text(0), r.Text(1), r.IsNull(2) ? -1 : r.Long(2)), args);
        if (refs.FirstOrDefault(r => r.Version < 0) is { } missing) throw new InvalidOperationException("message_name_unavailable:" + missing.Machine);
        if (refs.GroupBy(r => r.Machine).FirstOrDefault(g => g.Count() > 200) is { } oversized)
            throw new InvalidOperationException("message_order_resource_limit:" + oversized.Key);
        if (refs.Count > 800) throw new InvalidOperationException("message_order_resource_limit:" + refs[^1].Machine);
        return refs;
    }

    public MessagePage ReceiverPage(MessageScope scope, MessageCursor cursor, string bucket, IReadOnlyList<MessageOrderInput> names, int limit, CancellationToken ct)
    {
        using var read = db.OpenSnapshot(ct); var sources = MessageCursors.Sources(read, machine);
        var json = JsonSerializer.Serialize(names.ToArray(), MessageReadModelJson.Default.MessageOrderInputArray);
        var args = Arguments(scope, cursor.After).Concat([new SqlArg("$bucket", bucket), new("$names", json), new("$limit", limit + 1)]).ToArray();
        var sql = NameEligible(scope) + """
            , ordered AS (
              SELECT e.origin_machine,e.id,e.origin_created_ms,
                CASE WHEN e.full_available=1 THEN e.full_name ELSE
                  (SELECT json_extract(j.value,'$.name') FROM json_each($names) j
                   WHERE json_extract(j.value,'$.key.originMachine')=e.recipient_machine
                     AND json_extract(j.value,'$.key.id')=e.to_session_id) END AS ordering_name
              FROM eligible e WHERE e.bucket COLLATE NOCASE=$bucket COLLATE NOCASE
            ), anchor AS (
              SELECT ordering_name,origin_created_ms,origin_machine,id FROM ordered
              WHERE origin_machine=$afterOrigin AND id=$afterId AND origin_created_ms=$afterTime
            )
            """;
        if (read.QueryFirst(sql + " SELECT count(*) FROM ordered WHERE ordering_name IS NULL", r => r.Long(0), args) != 0 ||
            cursor.After is not null && read.QueryFirst(sql + " SELECT count(*) FROM anchor", r => r.Long(0), args) != 1)
            throw new InvalidOperationException("query_changed");
        var rows = read.Query(sql + $"""
            SELECT {MessageMetadata.ItemColumns} FROM ordered d JOIN canonical c ON c.origin_machine=d.origin_machine AND c.id=d.id
            WHERE $afterId IS NULL OR EXISTS(SELECT 1 FROM anchor a WHERE d.ordering_name COLLATE NOCASE>a.ordering_name COLLATE NOCASE OR
              (d.ordering_name COLLATE NOCASE=a.ordering_name COLLATE NOCASE AND
                (d.origin_created_ms<a.origin_created_ms OR d.origin_created_ms=a.origin_created_ms AND
                 (d.origin_machine COLLATE BINARY>a.origin_machine COLLATE BINARY OR d.origin_machine=a.origin_machine AND d.id COLLATE BINARY>a.id COLLATE BINARY))))
            ORDER BY d.ordering_name COLLATE NOCASE,d.origin_created_ms DESC,d.origin_machine COLLATE BINARY,d.id COLLATE BINARY LIMIT $limit
            """, r => Read(r), args);
        var more = rows.Count > limit; if (more) rows.RemoveAt(limit);
        var next = more ? cursor with { After = rows[^1].Order } : cursor with { After = null, Name = new(bucket, [], true) };
        var later = more || HasLater(read, scope, bucket);
        return NamePage(read, scope, next, rows.Select(r => r.Item).ToArray(), later, sources);
    }

    private bool HasLater(IReadSnapshot read, MessageScope scope, string bucket) => read.QueryFirst(NameEligible(scope) +
        " SELECT 1 FROM eligible WHERE bucket COLLATE NOCASE>$bucket COLLATE NOCASE LIMIT 1", r => r.Long(0), Arguments(scope, null).Append(new SqlArg("$bucket", bucket)).ToArray()) == 1;

    public IReadOnlyList<MessageScopeCoverage> SenderCoverage(MessageScope scope, MessageCursor cursor, string bucket, CancellationToken ct)
    {
        using var read = db.OpenSnapshot(ct);
        var result = read.Query(NameEligible(scope) + """
            SELECT origin_machine,count(*) FROM eligible WHERE bucket COLLATE NOCASE=$bucket COLLATE NOCASE
            GROUP BY origin_machine ORDER BY origin_machine LIMIT 801
            """, r => new MessageScopeCoverage(r.Text(0), r.Long(1)), Arguments(scope, null).Append(new SqlArg("$bucket", bucket)).ToArray());
        if (result.Count > 800) throw new InvalidOperationException("message_order_resource_limit:" + result[^1].Machine); return result;
    }

    public IReadOnlyList<MessageDisplay> OrderIntersection(MessageScope scope, MessageCursor cursor, IReadOnlyList<MessageOrderInput> inputs, CancellationToken ct)
    {
        if (inputs.Count > 200) throw new InvalidOperationException("message_order_resource_limit");
        using var read = db.OpenSnapshot(ct); var json = JsonSerializer.Serialize(inputs.ToArray(), MessageReadModelJson.Default.MessageOrderInputArray);
        return read.Query(MessageMetadata.Cte + $"""
            SELECT {MessageMetadata.ItemColumns} FROM canonical c JOIN json_each($inputs) j
              ON c.origin_machine=json_extract(j.value,'$.key.originMachine') AND c.id=json_extract(j.value,'$.key.id') AND c.origin_created_ms=json_extract(j.value,'$.createdMs')
            WHERE {MessageMetadata.Scope}
            ORDER BY CASE WHEN c.origin_machine=$local THEN c.sender_order_name ELSE json_extract(j.value,'$.name') END COLLATE NOCASE,c.origin_created_ms DESC,c.origin_machine COLLATE BINARY,c.id COLLATE BINARY
            """, r => Read(r).Item, Arguments(scope, null).Append(new SqlArg("$inputs", json)).ToArray());
    }

    public int FirstOrderHead(IReadOnlyList<MessageOrderInput> heads, CancellationToken ct)
    {
        using var read = db.OpenSnapshot(ct); var json = JsonSerializer.Serialize(heads.ToArray(), MessageReadModelJson.Default.MessageOrderInputArray);
        return read.QueryFirst("""
            SELECT CAST(j.key AS INTEGER) FROM json_each($heads) j
            ORDER BY CASE WHEN json_extract(j.value,'$.key.originMachine')=$local THEN
              (SELECT m.sender_label FROM engine_message m WHERE m.machine=$local AND m.origin_machine=$local AND m.id=json_extract(j.value,'$.key.id'))
              ELSE json_extract(j.value,'$.name') END COLLATE NOCASE,json_extract(j.value,'$.createdMs') DESC,
              json_extract(j.value,'$.key.originMachine') COLLATE BINARY,json_extract(j.value,'$.key.id') COLLATE BINARY LIMIT 1
            """, r => checked((int)r.Long(0)), new SqlArg("$heads", json), new("$local", machine));
    }

    public MessagePage SenderPage(MessageScope scope, MessageCursor cursor, IReadOnlyList<SenderPosition> positions, IReadOnlyList<MessageDisplay> items, CancellationToken ct)
    {
        using var read = db.OpenSnapshot(ct); var sources = MessageCursors.Sources(read, machine); var bucket = cursor.Name!.Bucket;
        var done = positions.All(p => p.End); var next = cursor with { After = null, Name = new(bucket, done ? [] : positions, done) };
        return NamePage(read, scope, next, items, !done || HasLater(read, scope, bucket), sources);
    }
    public void ValidateNameRead(MessageScope scope, MessageCursor cursor, CancellationToken ct)
    {
        using var read = db.OpenSnapshot(ct); var sources = MessageCursors.Sources(read, machine);
        MessageCursors.Validate(read, cursor, MessageCursors.Hash(scope, sources), sources, true);
        if (scope.Sort != "receiver") return;
        foreach (var cut in cursor.Cuts)
            if (read.QueryFirst(MessageMetadata.Cte + $"""
                SELECT 1 FROM engine_event e WHERE e.machine=$source AND e.seq>$through AND e.entity_kind='session'
                  AND EXISTS(SELECT 1 FROM canonical c WHERE {MessageMetadata.Scope}
                    AND c.recipient_machine=e.machine AND c.to_session_id=e.entity_id) LIMIT 1
                """, r => r.Long(0), Arguments(scope, null).Concat([new SqlArg("$source", cut.Machine), new("$through", cut.Through)]).ToArray()) == 1)
                throw new InvalidOperationException("query_changed");
    }

    public SenderOrderPage OwningSender(SenderOrderArgs args, CancellationToken ct) => new MessageOrderQueries(db, machine).Sender(args, ct);
    public LocalSenderPage LocalSender(SenderOrderArgs args, CancellationToken ct) => new MessageOrderQueries(db, machine).LocalHeads(args, ct);
    public ReceiverOrderPage OwningReceiver(ReceiverOrderArgs args, CancellationToken ct) => new MessageOrderQueries(db, machine).Receiver(args, ct);

}
