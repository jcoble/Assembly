using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine.Reading;

public sealed class EngineReader(IAppDatabase db, string machine)
{
    public long HighWatermark(CancellationToken cancellationToken)
    {
        using var read = db.OpenSnapshot(cancellationToken);
        return ReadMeta(read, machine).High;
    }

    public StreamPage Since(string topic, string? key, long after, CancellationToken cancellationToken, long? through = null)
    {
        var kinds = DisplayQueries.Kinds(topic);
        using var read = db.OpenSnapshot(cancellationToken);
        var meta = ReadMeta(read, machine);
        if (after > meta.High) throw new ArgumentException("cursor_ahead");
        if (through is { } cut) meta.High = Math.Min(meta.High, cut);
        if (after < meta.Floor) return new(after, after, [], false, true);

        // Bound the global scan before filtering: unrelated topics are not gaps.
        var scanned = read.Query("""
            SELECT machine,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json
            FROM engine_event WHERE seq>$after AND seq<=$high ORDER BY seq LIMIT 200
            """, r => new EngineEvent(r.Text(0), r.Long(1), r.Text(2), r.Text(3), r.Text(4),
                r.Long(5), r.Long(6), r.Long(7) != 0,
                JsonSerializer.Deserialize(r.Text(8), EngineJson.Default.EngineEventData)!),
            new SqlArg("$after", after), new SqlArg("$high", meta.High));
        var scannedThrough = after;
        List<EngineEvent> events = [];
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StreamPage(after, meta.High, [], false, false),
            EngineJson.Default.StreamPage).Length;
        foreach (var item in scanned)
        {
            if (item.Seq != scannedThrough + 1) return new(after, scannedThrough, events, false, true);
            if (Matches(item, topic, key, kinds))
            {
                var size = JsonSerializer.SerializeToUtf8Bytes(item, EngineJson.Default.EngineEvent).Length;
                if (size > 32 * 1024) throw new InvalidOperationException("Event exceeds the 32 KiB UTF-8 budget.");
                if (bytes + size + 1 > 64 * 1024) break;
                bytes += size + 1;
                events.Add(item);
            }
            scannedThrough = item.Seq;
        }
        if (scanned.Count < 200 && (scanned.Count == 0 || scannedThrough == scanned[^1].Seq) && scannedThrough < meta.High)
            return new(after, scannedThrough, events, false, true);
        return new(after, scannedThrough, events, scannedThrough < meta.High, false);
    }

    private static bool Matches(EngineEvent item, string topic, string? key, string[] kinds)
    {
        if (!kinds.Contains(item.EntityKind)) return false;
        if (key is null) return true;
        return (topic, item.EntityKind) switch
        {
            ("runs", "step" or "handoff") => item.Data.RunId == key,
            ("sessions", "handoff") => item.Data.SessionId == key,
            _ => item.EntityId == key
        };
    }

    public static bool Matches(EngineEvent item, string topic, string? key) =>
        Matches(item, topic, key, DisplayQueries.Kinds(topic));

    public JsonElement? Entity(string entityKind, string entityId, CancellationToken cancellationToken)
    {
        var projection = DisplayQueries.Json(entityKind); // validates the table allowlist
        using var read = db.OpenSnapshot(cancellationToken);
        var json = read.QueryFirst($"SELECT {projection} FROM engine_{entityKind} t WHERE t.id=$id AND t.machine=$machine",
            r => r.Text(0), new("$id", entityId), new("$machine", machine));
        return json is null ? null : DisplayQueries.Parse(json);
    }

    public EngineSnapshot Snapshot(string topic, string? key, CancellationToken cancellationToken)
    {
        var kinds = DisplayQueries.Kinds(topic);
        var read = db.OpenSnapshot(cancellationToken);
        try { return new(read, ReadMeta(read, machine).High, topic, key, kinds, machine); }
        catch { read.Dispose(); throw; }
    }

    private static (long High, long Floor) ReadMeta(IReadSnapshot read, string machine) =>
        read.QueryFirst("SELECT next_seq-1,retained_after,machine_id FROM engine_meta WHERE singleton=1",
            r => r.Text(2) == machine ? (r.Long(0), r.Long(1)) : throw new ArgumentException("wrong_machine"));
}
