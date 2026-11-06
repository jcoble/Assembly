using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Reading;

namespace Assembly.Engine.Messages.Reading;

/// <summary>Owning full-name reads on the existing bulk lane; no delivery or operational authority.</summary>
public sealed class MessageOrderQueries(IAppDatabase db, string machine)
{
    public SenderOrderPage Sender(SenderOrderArgs args, CancellationToken ct) => ReadSender(args, ct, true);

    public LocalSenderPage LocalHeads(SenderOrderArgs args, CancellationToken ct)
    {
        var page = ReadSender(args, ct, false);
        return new(page.Items.Select(f => new LocalSenderHead(f.Key, f.CreatedMs, f.CreatedSeq)).ToArray(), page.End);
    }

    private SenderOrderPage ReadSender(SenderOrderArgs args, CancellationToken ct, bool includeNames)
    {
        if (args.Bucket is null || args.Bucket.EnumerateRunes().Count() > 256 || args.Limit is < 1 or > 200 || args.Through < 0 ||
            args.After is { } after && (after.OriginMachine != machine || string.IsNullOrEmpty(after.Id))) throw new ArgumentException("bad_args");
        using var read = db.OpenSnapshot(ct);
        var valid = read.QueryFirst("SELECT retained_after,next_seq-1 FROM engine_meta WHERE singleton=1",
            r => (Floor: r.Long(0), High: r.Long(1)));
        if (args.Through < valid.Floor || args.Through > valid.High ||
            (args.Through != 0 || args.At is not null) && (EngineReader.EventAt(read, args.Through, machine) is not { } recorded || recorded != args.At))
            throw new InvalidOperationException("query_changed");
        const string stream = """
            WITH stream AS (
              SELECT m.id,m.created_ms,m.message_created_seq,m.sender_label,
                octet_length(m.sender_label) AS bytes,
                CASE WHEN $names=0 THEN '' WHEN octet_length(m.sender_label)<=32768 THEN m.sender_label END AS bounded_name
              FROM engine_message m WHERE m.machine=$machine AND m.origin_machine=$machine
                AND (m.message_created_seq IS NULL OR m.message_created_seq<=$through)
                AND substr(m.sender_label,1,256) COLLATE NOCASE=$bucket COLLATE NOCASE
            ), anchor AS (SELECT id,created_ms,sender_label FROM stream WHERE id=$afterId AND created_ms=$afterTime)
            """;
        SqlArg[] parameters = [new("$machine",machine),new("$through",args.Through),new("$bucket",args.Bucket),
            new("$names",includeNames?1:0),new("$afterId",args.After?.Id),new("$afterTime",args.After?.OriginCreatedMs),new("$limit",args.Limit+1)];
        if (args.After is not null && read.QueryFirst(stream + " SELECT count(*) FROM anchor", r => r.Long(0), parameters) != 1)
            throw new InvalidOperationException("query_changed");
        var rows = read.Query(stream + """
            SELECT id,created_ms,message_created_seq,bytes,bounded_name FROM stream s
            WHERE $afterId IS NULL OR EXISTS(SELECT 1 FROM anchor a WHERE
              s.sender_label COLLATE NOCASE>a.sender_label COLLATE NOCASE OR
              (s.sender_label COLLATE NOCASE=a.sender_label COLLATE NOCASE AND
                (s.created_ms<a.created_ms OR s.created_ms=a.created_ms AND s.id COLLATE BINARY>a.id COLLATE BINARY)))
            ORDER BY sender_label COLLATE NOCASE,created_ms DESC,id COLLATE BINARY LIMIT $limit
            """, r => (Id: r.Text(0), Created: r.Long(1), Seq: r.IsNull(2) ? (long?)null : r.Long(2), Bytes: r.Long(3), Name: r.IsNull(4) ? null : r.Text(4)), parameters);
        List<SenderOrderFact> items = []; var bytes = 0; var end = rows.Count <= args.Limit;
        foreach (var row in rows.Take(args.Limit))
        {
            ct.ThrowIfCancellationRequested();
            if (includeNames && row.Bytes > 32768 || row.Name is null) throw new InvalidOperationException("message_response_too_large");
            var fact = new SenderOrderFact(new(machine, row.Id), row.Created, row.Seq, row.Name);
            var size = JsonSerializer.SerializeToUtf8Bytes(fact, MessageReadModelJson.Default.SenderOrderFact).Length;
            if (size > 32768) throw new InvalidOperationException("message_response_too_large");
            if (bytes + size > 1024 * 1024 - 4096) { end = false; break; }
            bytes += size; items.Add(fact);
        }
        var page = new SenderOrderPage(items, end); Budget(JsonSerializer.SerializeToUtf8Bytes(page, MessageReadModelJson.Default.SenderOrderPage)); return page;
    }

    public ReceiverOrderPage Receiver(ReceiverOrderArgs args, CancellationToken ct)
    {
        if (args.Refs is null || args.Refs.Count is < 1 or > 200 || args.Refs.Any(r => r is null || string.IsNullOrWhiteSpace(r.SessionId) || r.Version < 0) ||
            args.Refs.Select(r => r.SessionId).Distinct(StringComparer.Ordinal).Count() != args.Refs.Count) throw new ArgumentException("bad_args");
        using var read = db.OpenSnapshot(ct);
        var refs = JsonSerializer.Serialize(args.Refs.ToArray(), MessageReadModelJson.Default.ReceiverOrderRefArray);
        var rows = read.Query("""
            SELECT s.id,s.version,octet_length(s.title),CASE WHEN octet_length(s.title)<=32768 THEN s.title END
            FROM json_each($refs) j JOIN engine_session s ON s.id=json_extract(j.value,'$.sessionId')
              AND s.version=json_extract(j.value,'$.version') AND s.machine=$machine
            ORDER BY s.title COLLATE NOCASE,s.id COLLATE BINARY
            """, r => (Id: r.Text(0), Version: r.Long(1), Bytes: r.Long(2), Name: r.IsNull(3) ? null : r.Text(3)), new SqlArg("$refs", refs), new("$machine", machine));
        if (rows.Count != args.Refs.Count) throw new InvalidOperationException("query_changed");
        List<ReceiverOrderFact> items = [];
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested(); if (row.Bytes > 32768 || row.Name is null) throw new InvalidOperationException("message_response_too_large");
            var fact = new ReceiverOrderFact(row.Id, row.Version, row.Name);
            if (JsonSerializer.SerializeToUtf8Bytes(fact, MessageReadModelJson.Default.ReceiverOrderFact).Length > 32768)
                throw new InvalidOperationException("message_response_too_large");
            items.Add(fact);
        }
        var result = new ReceiverOrderPage(items); Budget(JsonSerializer.SerializeToUtf8Bytes(result, MessageReadModelJson.Default.ReceiverOrderPage)); return result;
    }

    private static void Budget(byte[] bytes)
    { if (bytes.Length > 1024 * 1024 - 1024) throw new InvalidOperationException("message_response_too_large"); }
}
