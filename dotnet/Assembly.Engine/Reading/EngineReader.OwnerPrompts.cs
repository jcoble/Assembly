using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Engine.Messages;

namespace Assembly.Engine.Reading;

public sealed partial class EngineReader
{
    private sealed record Pending(long Created, OwnerPromptManifest Item);

    public OwnerPromptPage OwnerPrompts(OwnerPromptReadArgs args, Func<OwnerPromptPage, int> encodedBytes, CancellationToken ct)
    {
        ValidateOwner(args.Session, args.Machine);
        if (args.Limit is < 1 or > 200 || (args.AfterCreatedMs is null) != (args.AfterClientId is null) ||
            args.AfterClientId is { } client && !ValidClient(client)) throw new ArgumentException("bad_args");
        using var read = db.OpenSnapshot(ct);
        var rows = read.Query("""
            SELECT created_ms,client_id,owner_content_sha256,substr(text,1,256),
                length(CAST(text AS BLOB)),length(CAST(attachment_ids_json AS BLOB)),json_array_length(attachment_ids_json),
                target_epoch,held_epoch,state,version
            FROM engine_message WHERE machine=$machine AND to_session_id=$session AND sender_kind='window' AND framed=0
                AND state IN ('queued','held') AND
                ($after IS NULL OR created_ms>$after OR (created_ms=$after AND client_id>$client))
            ORDER BY created_ms,client_id LIMIT $limit
            """, r => new Pending(r.Long(0), new(r.Text(1), r.Text(2), r.Text(3),
                Encoding.UTF8.GetByteCount(r.Text(3)) == r.Long(4), r.Long(4), r.Long(5), r.Long(6),
                r.IsNull(7) ? null : r.Long(7), r.Long(8), r.Text(9), r.Long(10))),
            new("$machine", machine), new("$session", args.Session), new("$after", args.AfterCreatedMs),
            new("$client", args.AfterClientId), new("$limit", args.Limit + 1));
        List<OwnerPromptManifest> items = [];
        OwnerPromptPage page = new(machine, items, null, null);
        for (var i = 0; i < Math.Min(args.Limit, rows.Count); i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];
            var more = i + 1 < rows.Count;
            var candidate = new OwnerPromptPage(machine, [.. items, row.Item], more ? row.Created : null,
                more ? row.Item.ClientId : null);
            if (encodedBytes(candidate) > 64 * 1024)
            {
                if (items.Count == 0) throw new InvalidOperationException("Owner manifest exceeds response budget.");
                var last = rows[i - 1];
                page = new(machine, items, last.Created, last.Item.ClientId);
                break;
            }
            items.Add(row.Item);
            page = candidate;
        }
        if (encodedBytes(page) > 64 * 1024) throw new InvalidOperationException("Owner response exceeds budget.");
        ct.ThrowIfCancellationRequested();
        return page;
    }

    public OwnerPromptChunk OwnerPromptContent(OwnerPromptChunkArgs args, CancellationToken ct)
    {
        ValidateOwner(args.Session, args.Machine);
        if (string.IsNullOrEmpty(args.ClientId) || string.IsNullOrEmpty(args.ContentSha256) || !ValidClient(args.ClientId) || args.Offset < 0 || args.Count is < 1 or > 1024 * 1024 ||
            args.Part is not ("text" or "attachments") || args.ContentSha256.Length != 64 ||
            args.ContentSha256.Any(ch => !(ch is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("bad_args");
        var column = args.Part == "text" ? "text" : "attachment_ids_json";
        using var read = db.OpenSnapshot(ct);
        var metadata = read.QueryFirst($"""
            SELECT owner_content_sha256,length(CAST({column} AS BLOB)) FROM engine_message
            WHERE id=$id AND machine=$machine AND to_session_id=$session AND client_id=$client AND sender_kind='window' AND framed=0
            """, r => (Digest: r.Text(0), Length: r.Long(1)),
            new("$id", OwnerPromptIdentity.MessageId(args.Session, args.ClientId)), new("$machine", machine),
            new("$session", args.Session), new("$client", args.ClientId));
        if (metadata.Digest is null) throw new KeyNotFoundException("unknown owner prompt");
        if (metadata.Digest != args.ContentSha256) throw new InvalidOperationException("owner_prompt_changed");
        if (args.Offset > metadata.Length) throw new ArgumentException("bad_args");
        var take = Math.Min(args.Count, metadata.Length - args.Offset);
        var bytes = take == 0 ? [] : read.QueryFirst($"""
            SELECT substr(CAST({column} AS BLOB),$offset,$take) FROM engine_message WHERE id=$id AND machine=$machine
            """, r => r.Blob(0), new("$offset", checked(args.Offset + 1)), new("$take", take),
            new("$id", OwnerPromptIdentity.MessageId(args.Session, args.ClientId)), new("$machine", machine))!;
        ct.ThrowIfCancellationRequested();
        var end = args.Offset + bytes.LongLength;
        return new(metadata.Digest, args.Part, args.Offset, metadata.Length, bytes, end < metadata.Length ? end : null);
    }

    private void ValidateOwner(string session, string? expected)
    {
        if (string.IsNullOrWhiteSpace(session)) throw new ArgumentException("bad_args");
        if (expected is not null && expected != machine) throw new InvalidOperationException("wrong_machine");
    }

    private static bool ValidClient(string value) => value.Length is >= 1 and <= 64 && value.All(ch =>
        ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');
}
