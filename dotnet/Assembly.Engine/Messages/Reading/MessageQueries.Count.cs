using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Reading;

namespace Assembly.Engine.Messages.Reading;

public sealed partial class MessageQueries
{
    public MessageCount Count(MessageSessionKey session, IReadOnlyList<SeenCut> seen, CancellationToken ct, string? storage = null)
    {
        if (session is null || seen is null || seen.Any(cut => cut is null || cut.Through < 0 || string.IsNullOrWhiteSpace(cut.Machine)) ||
            seen.Select(cut => cut.Machine).Distinct(StringComparer.Ordinal).Count() != seen.Count) throw new ArgumentException("bad_args");
        var scope = new MessageScope(storage, session.Machine, session.Id, null, null, null, "", "time"); Validate(scope, 100);
        using var read = db.OpenSnapshot(ct); var sources = MessageCursors.Sources(read, machine);
        var cuts = sources.Select(source => new MessageCut(source.Machine, source.High, source.At, source.Generation)).ToArray();
        var partial = false; List<SeenCut> valid = []; List<string> invalid = [];
        foreach (var cut in seen)
        {
            var source = sources.SingleOrDefault(source => source.Machine == cut.Machine);
            if (source is null || !MessageCursors.MatchesCut(read, cut.Through, cut.At, cut.Generation, source)) { partial = true; invalid.Add(cut.Machine); continue; }
            valid.Add(cut);
        }
        var json = JsonSerializer.Serialize(valid.ToArray(), MessageReadModelJson.Default.SeenCutArray);
        var args = Arguments(scope, null).Concat([new SqlArg("$seen", json), new("$invalid", JsonSerializer.Serialize(invalid.ToArray(), MessageReadModelJson.Default.StringArray))]).ToArray();
        var count = read.QueryFirst(MessageMetadata.Cte + $"""
            SELECT COUNT(*) FROM canonical c WHERE {MessageMetadata.Scope} AND c.message_created_seq IS NOT NULL
              AND NOT EXISTS(SELECT 1 FROM json_each($invalid) WHERE value=c.origin_machine)
              AND c.message_created_seq>COALESCE((SELECT json_extract(value,'$.through') FROM json_each($seen)
                WHERE json_extract(value,'$.machine')=c.origin_machine),0)
            """, row => row.Long(0), args);
        partial |= read.QueryFirst(MessageMetadata.Cte + $"SELECT COUNT(*) FROM canonical c WHERE {MessageMetadata.Scope} AND c.message_created_seq IS NULL", row => row.Long(0), Arguments(scope, null)) > 0;
        var missing = Missing(read, scope, sources);
        return new(count, cuts, missing, partial || missing.Length > 0 || Unknown(read, scope).Length > 0)
        { Captured = new(MessageCursors.Hash(scope, sources), cuts, null) };
    }
    public void ValidateCount(MessageSessionKey session, string? storage, MessageCount count, CancellationToken ct)
    {
        var scope = new MessageScope(storage, session.Machine, session.Id, null, null, null, "", "time");
        using var read = db.OpenSnapshot(ct); var sources = MessageCursors.Sources(read, machine);
        MessageCursors.Validate(read, count.Captured ?? throw new ArgumentException("bad_args"), MessageCursors.Hash(scope, sources), sources);
    }

}
