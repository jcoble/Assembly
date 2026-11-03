using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Messages.Reading;

/// <summary>Bounded read snapshots; all metadata filtering, canonical copy selection and keysets stay in SQL.</summary>
public sealed partial class MessageQueries(IAppDatabase db, string machine)
{
    public static MessageCursor DecodeCursor(string value) => MessageCursors.Decode(value);

    public MessagePage Candidates(MessageScope scope, MessageCursor? cursor, int limit, CancellationToken ct)
    {
        Validate(scope, limit); ct.ThrowIfCancellationRequested();
        using var read = db.OpenSnapshot(ct);
        var sources = MessageCursors.Sources(read, machine); var hash = MessageCursors.Hash(scope, sources);
        cursor ??= new(hash, sources.Select(source => new MessageCut(source.Machine, source.High, source.At, source.Generation)).ToArray(), null);
        MessageCursors.Validate(read, cursor, hash, sources);
        var args = Arguments(scope, cursor.After).Concat([new SqlArg("$limit", limit + 1)]).ToArray();
        var rows = read.Query(MessageMetadata.Cte + $"""
            , ordered AS (SELECT c.*,{MessageMetadata.Name(scope.Sort)} AS ordering_name FROM canonical c WHERE {MessageMetadata.Scope})
            SELECT origin_machine,id,storage_machine,version,sender_machine,from_session_id,COALESCE(sender_name,''),
                recipient_machine,to_session_id,COALESCE(receiver_name,''),run_machine,run_id,origin_created_ms,kind,state,delivery,reason,
                COALESCE(preview,''),ordering_name FROM ordered c
            WHERE {MessageMetadata.After} ORDER BY ordering_name COLLATE NOCASE,origin_created_ms DESC,origin_machine COLLATE BINARY,id COLLATE BINARY LIMIT $limit
            """, row => Read(row), args);
        var more = rows.Count > limit; if (more) rows.RemoveAt(limit);
        var after = rows.Count == 0 ? cursor.After : rows[^1].Order;
        var captured = cursor with { After = after };
        var result = new MessagePage(rows.Select(row => row.Item).ToArray(), more ? MessageCursors.Encode(captured) : null,
            cursor.Cuts, Missing(read, scope, sources), Unknown(read, scope))
        { Captured = captured };
        Budget(result); return result;
    }

    public MessagePage SelectMatches(MessageScope scope, MessagePage candidates, IReadOnlyList<MessageKey> matches, CancellationToken ct)
    {
        Validate(scope, Math.Max(1, candidates.Items.Count));
        if (matches is null || matches.Count > 800 || candidates.Items.Count > 200) throw new ArgumentException("bad_args");
        var captured = candidates.Captured ?? throw new ArgumentException("bad_args");
        using var read = db.OpenSnapshot(ct); var sources = MessageCursors.Sources(read, machine);
        MessageCursors.Validate(read, captured, MessageCursors.Hash(scope, sources), sources);
        var keys = JsonSerializer.Serialize(candidates.Items.Select(item => item.Key).ToArray(), MessageReadModelJson.Default.MessageKeyArray);
        var matched = JsonSerializer.Serialize(matches.ToArray(), MessageReadModelJson.Default.MessageKeyArray);
        var args = Arguments(scope, null).Concat([new SqlArg("$keys", keys), new SqlArg("$matches", matched)]).ToArray();
        var items = read.Query(MessageMetadata.Cte + $"""
            SELECT c.origin_machine,c.id,c.storage_machine,c.version,c.sender_machine,c.from_session_id,COALESCE(c.sender_name,''),
                c.recipient_machine,c.to_session_id,COALESCE(c.receiver_name,''),c.run_machine,c.run_id,c.origin_created_ms,c.kind,c.state,c.delivery,c.reason,
                COALESCE(c.preview,''),{MessageMetadata.Name(scope.Sort)} FROM canonical c
            WHERE {MessageMetadata.Scope}
              AND EXISTS(SELECT 1 FROM json_each($keys) k WHERE json_extract(k.value,'$.originMachine')=c.origin_machine AND json_extract(k.value,'$.id')=c.id)
              AND ($search='' OR EXISTS(SELECT 1 FROM json_each($matches) m WHERE json_extract(m.value,'$.originMachine')=c.origin_machine AND json_extract(m.value,'$.id')=c.id)
                OR instr(lower(COALESCE(c.sender_name,'')),lower($search))>0 OR instr(lower(COALESCE(c.receiver_name,'')),lower($search))>0)
            ORDER BY {MessageMetadata.Order(scope.Sort)}
            """, row => Read(row).Item, args);
        var result = candidates with { Items = items }; Budget(result); return result;
    }

    public void ValidatePage(MessageScope scope, MessagePage page, CancellationToken ct)
    {
        using var read = db.OpenSnapshot(ct); var sources = MessageCursors.Sources(read, machine);
        MessageCursors.Validate(read, page.Captured ?? throw new ArgumentException("bad_args"), MessageCursors.Hash(scope, sources), sources);
    }

    private static void Validate(MessageScope scope, int limit)
    {
        if (scope is null || limit is < 1 or > 200 || scope.Search is null || scope.Search.EnumerateRunes().Count() > 256 ||
            scope.Sort is not ("time" or "sender" or "receiver") || scope.State is not (null or "held" or "queued" or "delivered_to_turn" or "next_turn" or "failed") ||
            (scope.SessionMachine is null) != (scope.SessionId is null) || (scope.RunMachine is null) != (scope.RunId is null) ||
            new[] { scope.Machine, scope.SessionMachine, scope.SessionId, scope.RunMachine, scope.RunId }.Any(value => value is not null && string.IsNullOrWhiteSpace(value))) throw new ArgumentException("bad_args");
    }

    private SqlArg[] Arguments(MessageScope scope, MessageOrder? after) =>
    [ new("$local",machine),new("$storage",scope.Machine),new("$sessionMachine",scope.SessionMachine),new("$session",scope.SessionId),
      new("$runMachine",scope.RunMachine),new("$run",scope.RunId),new("$state",scope.State),new("$search",scope.Search),
      new("$afterName",after?.Name),new("$afterTime",after?.OriginCreatedMs),new("$afterOrigin",after?.OriginMachine),new("$afterId",after?.Id) ];

    private static (MessageDisplay Item, MessageOrder Order) Read(IRow row)
    {
        string? Text(int at) => row.IsNull(at) ? null : row.Text(at);
        var origin = row.Text(0); var id = row.Text(1); var created = row.Long(12);
        return (new(new(origin, id), new(row.Text(2), id), row.Long(3), row.Text(4), Text(5), row.Text(6), row.Text(7), row.Text(8), row.Text(9),
            Text(10), Text(11), created, row.Text(13), row.Text(14), row.Text(15), Text(16), row.Text(17), true), new(row.Text(18), created, origin, id));
    }

    private MessageStorage[] Unknown(IReadSnapshot read, MessageScope scope) => read.Query(MessageMetadata.Cte + """
        SELECT DISTINCT p.storage_machine,p.id FROM physical p WHERE ($storage IS NULL OR p.storage_machine=$storage)
          AND NOT EXISTS(SELECT 1 FROM canonical c WHERE c.origin_machine=p.origin_machine AND c.id=p.id)
        ORDER BY p.storage_machine,p.id LIMIT 200
        """, row => new MessageStorage(row.Text(0), row.Text(1)), new SqlArg("$local", machine), new("$storage", scope.Machine)).ToArray();

    private static string[] Missing(IReadSnapshot read, MessageScope scope, IReadOnlyList<MessageSource> sources)
    {
        var missing = read.Query("SELECT machine FROM engine_mirror WHERE state='snapshotting' AND ($storage IS NULL OR machine=$storage)",
            row => row.Text(0), new SqlArg("$storage", scope.Machine));
        if (scope.Machine is { } selected && !sources.Any(source => source.Machine == selected)) missing.Add(selected);
        return missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static void ValidateBudget(MessagePage page) => Budget(page);

    private static void Budget(MessagePage page)
    {
        foreach (var item in page.Items)
            if (JsonSerializer.SerializeToUtf8Bytes(item, MessageReadModelJson.Default.MessageDisplay).Length > 32 * 1024) throw new InvalidOperationException("message_response_too_large");
        // Leave room for the existing result frame and its correlation ID.
        if (JsonSerializer.SerializeToUtf8Bytes(page, MessageReadModelJson.Default.MessagePage).Length > 1024 * 1024 - 1024) throw new InvalidOperationException("message_response_too_large");
    }
}
