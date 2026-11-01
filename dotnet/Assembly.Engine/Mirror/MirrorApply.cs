using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Reading;

namespace Assembly.Engine.Mirror;

public sealed record MirrorCursor(string Machine, string Name, long Generation, long Through, CursorAt? At, string State);
public sealed record MirrorNotice(long Through, bool Reset);
public sealed class MirrorGap : Exception;

/// <summary>Copies display state inside the host's existing writer. No transitions or effects run here.</summary>
public static class MirrorApply
{
    public static MirrorCursor Open(IWriteScope s, string machine, string name, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(name) ||
            s.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) == machine)
            throw new ArgumentException("Invalid mirror machine");
        s.Execute("""
            DELETE FROM engine_mirror_row WHERE machine IN (SELECT machine FROM engine_mirror WHERE name=$name AND machine<>$machine);
            DELETE FROM engine_event WHERE machine IN (SELECT machine FROM engine_mirror WHERE name=$name AND machine<>$machine);
            UPDATE engine_mirror SET state='retired',updated_ms=$now WHERE name=$name AND machine<>$machine;
            INSERT INTO engine_mirror(machine,name,generation,through,retained_after,state,updated_ms)
            VALUES($machine,$name,0,0,0,'snapshotting',$now)
            ON CONFLICT(machine) DO UPDATE SET name=excluded.name,
                state=CASE WHEN engine_mirror.state='retired' THEN 'snapshotting' ELSE engine_mirror.state END,
                updated_ms=excluded.updated_ms;
            """, new("$name", name), new("$machine", machine), new("$now", nowMs));
        return Read(s, machine);
    }

    public static MirrorCursor BeginSnapshot(IWriteScope s, string machine, long nowMs)
    {
        var cursor = Read(s, machine);
        RequireActive(cursor);
        s.Execute("""
            DELETE FROM engine_mirror_row WHERE machine=$machine AND generation<>$generation;
            DELETE FROM engine_event WHERE machine=$machine;
            UPDATE engine_mirror SET state='snapshotting',updated_ms=$now WHERE machine=$machine;
            """, new("$machine", machine), new("$generation", cursor.Generation), new("$now", nowMs));
        return Read(s, machine);
    }

    public static void SnapshotPage(IWriteScope s, string machine, IReadOnlyList<JsonElement> items)
    {
        var cursor = Read(s, machine);
        if (cursor.State != "snapshotting") throw new MirrorGap();
        Rows(s, machine, checked(cursor.Generation + 1), items);
    }

    public static MirrorCursor EndSnapshot(IWriteScope s, string machine, long through, CursorAt? at, long nowMs)
    {
        var cursor = Read(s, machine);
        if (cursor.State != "snapshotting" || through < 0) throw new MirrorGap();
        var generation = checked(cursor.Generation + 1);
        s.Execute("""
            DELETE FROM engine_mirror_row WHERE machine=$machine AND generation<>$generation;
            UPDATE engine_mirror SET generation=$generation,through=$through,at_json=$at,retained_after=$through,
                state='live',updated_ms=$now WHERE machine=$machine;
            """, new("$machine", machine), new("$generation", generation), new("$through", through),
            new("$at", AtJson(at)), new("$now", nowMs));
        s.Publish("__engine_mirrored", machine, new MirrorNotice(through, true), EngineJson.Default.MirrorNotice);
        return Read(s, machine);
    }

    public static MirrorCursor Events(IWriteScope s, string machine, long after, long through, CursorAt? at,
        IReadOnlyList<EngineEvent> events, IReadOnlyList<JsonElement> rows, long nowMs)
    {
        var cursor = Read(s, machine);
        if (cursor.State != "live" || after != cursor.Through || through < after) throw new MirrorGap();
        var next = after;
        foreach (var item in events)
        {
            if (item.Machine != machine) throw new ArgumentException("Foreign mirror event");
            if (item.Seq <= after) continue;
            if (item.Seq != next + 1 || item.Seq > through) throw new MirrorGap();
            next = item.Seq;
            s.Execute("""
                INSERT OR IGNORE INTO engine_event(id,version,machine,created_ms,updated_ms,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json)
                VALUES($id,$version,$machine,$now,$now,$seq,$kind,$entityKind,$entityId,$version,$at,$late,$data)
                """, new("$id", $"{machine}:{item.Seq}"), new("$version", item.Version), new("$machine", machine),
                new("$now", nowMs), new("$seq", item.Seq), new("$kind", item.Kind), new("$entityKind", item.EntityKind),
                new("$entityId", item.EntityId), new("$at", item.AtMs), new("$late", item.Late ? 1 : 0),
                new("$data", JsonSerializer.Serialize(item.Data, EngineJson.Default.EngineEventData)));
        }
        if (next != through) throw new MirrorGap();
        Rows(s, machine, cursor.Generation, rows);
        s.Execute("UPDATE engine_mirror SET through=$through,at_json=$at,updated_ms=$now WHERE machine=$machine",
            new("$through", through), new("$at", AtJson(at)), new("$now", nowMs), new("$machine", machine));
        s.Publish("__engine_mirrored", machine, new MirrorNotice(through, false), EngineJson.Default.MirrorNotice);
        return Read(s, machine);
    }

    private static void Rows(IWriteScope s, string machine, long generation, IReadOnlyList<JsonElement> rows)
    {
        foreach (var row in rows)
        {
            var kind = row.GetProperty("entityKind").GetString();
            if (!DisplayQueries.Kinds("mirror").Contains(kind) || row.GetProperty("machine").GetString() != machine)
                throw new ArgumentException("Invalid mirror row");
            s.Execute("""
                INSERT INTO engine_mirror_row(machine,entity_kind,entity_id,generation,version,row_json)
                VALUES($machine,$kind,$id,$generation,$version,$json)
                ON CONFLICT(machine,generation,entity_kind,entity_id) DO UPDATE SET version=excluded.version,row_json=excluded.row_json
                WHERE excluded.version>engine_mirror_row.version;
                """, new("$machine", machine), new("$kind", kind), new("$id", row.GetProperty("id").GetString()),
                new("$generation", generation), new("$version", row.GetProperty("version").GetInt64()), new("$json", row.GetRawText()));
        }
    }

    private static void RequireActive(MirrorCursor cursor) { if (cursor.State == "retired") throw new MirrorGap(); }
    private static string? AtJson(CursorAt? at) => at is null ? null : JsonSerializer.Serialize(at, EngineJson.Default.CursorAt);
    private static MirrorCursor Read(IWriteScope s, string machine) => s.QueryFirst("""
        SELECT machine,name,generation,through,at_json,state FROM engine_mirror WHERE machine=$machine
        """, r => new MirrorCursor(r.Text(0), r.Text(1), r.Long(2), r.Long(3),
            r.IsNull(4) ? null : JsonSerializer.Deserialize(r.Text(4), EngineJson.Default.CursorAt), r.Text(5)), new SqlArg("$machine", machine))
        ?? throw new ArgumentException("Unknown mirror machine");
}
