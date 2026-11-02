using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;

namespace Assembly.Engine.Sessions;

/// <summary>Exact fixed-column title detail. Display previews never enter this path.</summary>
public static class SessionTitleContent
{
    private const string Authority = "(machine=$machine AND holder='local' AND state<>'foreign' OR state='foreign' AND machine<>$machine)";
    public static SessionTitleStat Stat(IReadScope scope, string machine, SessionTitleReadArgs args)
    {
        Validate(machine, args.Session, args.Machine);
        var row = scope.QueryFirst($"SELECT version,epoch,CAST(title AS BLOB),(SELECT machine_id FROM engine_meta WHERE singleton=1) FROM engine_session WHERE id=$id AND {Authority}",
            r => (Version: r.Long(0), Epoch: r.Long(1), Bytes: r.Blob(2), Machine: r.Text(3)), new("$id", args.Session), new("$machine", machine));
        if (row.Bytes is null) throw new KeyNotFoundException("unknown session");
        if (row.Machine != machine) throw new InvalidOperationException("title_changed");
        _ = new UTF8Encoding(false, true).GetCharCount(row.Bytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("session-title/v1"u8);
        Span<byte> length = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)row.Bytes.LongLength);
        hash.AppendData(length); hash.AppendData(row.Bytes);
        return new(row.Machine, row.Version, row.Epoch, row.Bytes.LongLength, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public static SessionTitleChunk Chunk(IReadScope scope, string machine, SessionTitleChunkArgs args)
    {
        Validate(machine, args.Session, args.Machine);
        if (args.Version < 0 || args.Epoch < 0 || args.TotalBytes < 0 || args.Offset < 0 || args.Offset > args.TotalBytes || args.Count is < 1 or > 1024 * 1024)
            throw new ArgumentException("bad_args");
        var take = Math.Min(args.Count, args.TotalBytes - args.Offset);
        var row = scope.QueryFirst($"""
            SELECT length(CAST(title AS BLOB)),substr(CAST(title AS BLOB),$offset,$take)
            FROM engine_session WHERE id=$id AND version=$version AND epoch=$epoch AND $machine=(SELECT machine_id FROM engine_meta WHERE singleton=1) AND {Authority}
            """, r => (Total: r.Long(0), Bytes: r.Blob(1)), new("$id", args.Session), new("$version", args.Version),
            new("$epoch", args.Epoch), new("$machine", machine), new("$offset", checked(args.Offset + 1)), new("$take", take));
        if (row.Bytes is null && scope.QueryFirst($"SELECT COUNT(*) FROM engine_session WHERE id=$id AND {Authority}", r => r.Long(0),
            new("$id", args.Session), new("$machine", machine)) == 0) throw new KeyNotFoundException("unknown session");
        if (row.Bytes is null || row.Total != args.TotalBytes || row.Bytes.LongLength != take)
            throw new InvalidOperationException("title_changed");
        var end = checked(args.Offset + row.Bytes.LongLength);
        return new(args.Version, args.Epoch, args.Offset, row.Total, row.Bytes, end < row.Total ? end : null);
    }

    private static void Validate(string machine, string session, string? expected)
    {
        if (string.IsNullOrWhiteSpace(session)) throw new ArgumentException("bad_args");
        if (expected is not null && expected != machine) throw new InvalidOperationException("title_changed");
    }
}
