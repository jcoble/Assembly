using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;

namespace Assembly.Application.Features.Chat.Sessions;

public sealed partial class SessionStore
{
    /// <summary>Reads the fixed legacy title in the caller's snapshot. The caller supplies its verified host identity.</summary>
    public LegacySessionTitleStat LegacyTitleStat(IReadScope scope, string publisherEngineMachine, LegacySessionTitleStatArgs args) =>
        new(ReadLegacyTitle(scope, publisherEngineMachine, args.Reference), null, null);

    public LegacySessionTitleChunk LegacyTitleChunk(IReadScope scope, string publisherEngineMachine, LegacySessionTitleChunkArgs args)
    {
        var reference = args.Reference;
        if (reference.Utf8Bytes < 0 || args.Offset < 0 || args.Offset > reference.Utf8Bytes || args.Count is < 1 or > 1024 * 1024)
            throw new ArgumentException("bad_args");
        var sqlOffset = checked(args.Offset + 1);
        var current = ReadLegacyTitle(scope, publisherEngineMachine, reference);
        var take = Math.Min(args.Count, current.Utf8Bytes - args.Offset);
        var data = scope.QueryFirst("SELECT COALESCE(substr(CAST(title AS BLOB),$offset,$take),X'') FROM session_info WHERE session_id=$id",
            row => row.Blob(0), new SqlArg("$id", reference.SessionId), new SqlArg("$offset", sqlOffset), new SqlArg("$take", take));
        if (data is null || data.LongLength != take) throw new InvalidOperationException("title_changed");
        var end = checked(args.Offset + data.LongLength);
        return new(args.Offset, current.Utf8Bytes, data, end < current.Utf8Bytes ? end : null);
    }

    private static LegacySessionTitleRef ReadLegacyTitle(IReadScope scope, string publisherEngineMachine, LegacySessionTitleRef reference)
    {
        if (string.IsNullOrWhiteSpace(reference.SessionId) || string.IsNullOrWhiteSpace(publisherEngineMachine) || reference.Utf8Bytes < 0)
            throw new ArgumentException("bad_args");
        if (reference.PublisherEngineMachine != publisherEngineMachine) throw new InvalidOperationException("title_changed");
        var actual = scope.QueryFirst("SELECT machine,parent_session_id,title_source,updated_at,CAST(title AS BLOB) FROM session_info WHERE session_id=$id",
            row => new LegacyTitleRow(row.IsNull(0) ? null : row.Text(0), row.IsNull(1) ? null : row.Text(1),
                row.IsNull(2) ? null : row.Text(2), row.Long(3), row.IsNull(4) ? null : row.Blob(4)), new SqlArg("$id", reference.SessionId));
        if (actual is null) throw new KeyNotFoundException("unknown session");
        if (actual.Bytes is null) throw new InvalidOperationException("title_changed");
        var hash = SessionTitleHash.Compute(actual.Bytes);
        if (actual.Machine != reference.StoredMachine || actual.Parent != reference.ParentSessionId || actual.Source != reference.TitleSource
            || actual.Bytes.LongLength != reference.Utf8Bytes || hash != reference.Sha256)
            throw new InvalidOperationException("title_changed");
        // UpdatedAt is observed metadata, never part of the stable content/provenance fence.
        return reference with { UpdatedAt = actual.UpdatedAt };
    }

    private sealed record LegacyTitleRow(string? Machine, string? Parent, string? Source, long UpdatedAt, byte[]? Bytes);
}
