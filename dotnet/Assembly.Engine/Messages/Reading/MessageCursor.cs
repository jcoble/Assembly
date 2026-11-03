using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Reading;

namespace Assembly.Engine.Messages.Reading;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SeenCut[]))]
[JsonSerializable(typeof(MessageCursor))]
[JsonSerializable(typeof(MessageKey[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(MessagePage))]
[JsonSerializable(typeof(MessageDisplay))]
[JsonSerializable(typeof(MessageBody))]
[JsonSerializable(typeof(BodyMatch))]
internal partial class MessageReadModelJson : JsonSerializerContext;

internal sealed record MessageSource(string Machine, long Generation, long High, long Floor, CursorAt? At);

internal static class MessageCursors
{
    internal static IReadOnlyList<MessageSource> Sources(IReadSnapshot read, string local)
    {
        var sources = read.Query("""
            SELECT machine_id,0,next_seq-1,retained_after,NULL FROM engine_meta WHERE singleton=1
            UNION ALL SELECT machine,generation,through,retained_after,at_json FROM engine_mirror WHERE state='live'
            ORDER BY 1
            """, row => new MessageSource(row.Text(0), row.Long(1), row.Long(2), row.Long(3),
                row.IsNull(4) ? null : JsonSerializer.Deserialize(row.Text(4), EngineJson.Default.CursorAt)));
        if (!sources.Any(source => source.Generation == 0 && source.Machine == local)) throw new InvalidOperationException("query_changed");
        return sources.Select(source => source.Generation == 0 ? source with { At = EngineReader.EventAt(read, source.High, source.Machine) } : source).ToArray();
    }

    internal static string Hash(MessageScope scope, IReadOnlyList<MessageSource> sources)
    {
        using var bytes = new MemoryStream();
        using (var json = new Utf8JsonWriter(bytes))
        {
            json.WriteStartArray();
            json.WriteStringValue("messages-query/logical-anchor-v2");
            foreach (var value in new[] { scope.Machine, scope.SessionMachine, scope.SessionId, scope.RunMachine, scope.RunId, scope.State, scope.Search, scope.Sort }) json.WriteStringValue(value);
            foreach (var source in sources) { json.WriteStringValue(source.Machine); json.WriteNumberValue(source.Generation); }
            json.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    internal static string Encode(MessageCursor cursor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, MessageReadModelJson.Default.MessageCursor));
    internal static MessageCursor Decode(string value)
    {
        if (value.Length > 1024 * 1024) throw new ArgumentException("bad_args");
        try
        {
            var cursor = JsonSerializer.Deserialize(Convert.FromBase64String(value), MessageReadModelJson.Default.MessageCursor) ?? throw new ArgumentException("bad_args");
            if (string.IsNullOrEmpty(cursor.QueryHash) || cursor.Cuts is null || cursor.Cuts.Any(cut => cut is null || string.IsNullOrEmpty(cut.Machine)) ||
                cursor.Cuts.Select(cut => cut.Machine).Distinct(StringComparer.Ordinal).Count() != cursor.Cuts.Count) throw new ArgumentException("bad_args");
            return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw new ArgumentException("bad_args", error); }
    }

    internal static void Validate(IReadSnapshot read, MessageCursor cursor, string hash, IReadOnlyList<MessageSource> sources)
    {
        if (cursor.QueryHash != hash || cursor.Cuts.Count != sources.Count) throw new InvalidOperationException("query_changed");
        foreach (var source in sources)
        {
            var cut = cursor.Cuts.SingleOrDefault(item => item.Machine == source.Machine);
            if (cut is null || cut.Through < source.Floor || cut.Through > source.High || cut.Through < 0 ||
                !MatchesCut(read, cut.Through, cut.At, cut.Generation, source) || read.QueryFirst("""
                    SELECT 1 FROM engine_event WHERE machine=$machine AND seq>$through AND entity_kind IN ('message','session','run') LIMIT 1
                    """, row => row.Long(0), new SqlArg("$machine", source.Machine), new("$through", cut.Through)) == 1)
                throw new InvalidOperationException("query_changed");
        }
    }
    internal static bool MatchesCut(IReadSnapshot read, long through, CursorAt? at, long? generation, MessageSource source)
    {
        if (through < source.Floor || through > source.High || through < 0 || generation is not null && generation != source.Generation) return false;
        var recorded = EngineReader.EventAt(read, through, source.Machine);
        if (recorded is not null) return recorded == at;
        // A committed mirror generation certifies continuity even when its snapshot-baseline
        // event is absent. Legacy cuts and local cuts still require a real fingerprint or zero.
        return source.Generation > 0 && generation == source.Generation || through == 0 && at is null;
    }

}
