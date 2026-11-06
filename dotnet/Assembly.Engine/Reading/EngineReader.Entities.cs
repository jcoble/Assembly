using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Reading;

public sealed record EngineEntityRef(string Kind, string Id);
public sealed record EngineEntitiesPage(IReadOnlyList<JsonElement> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Next = null);

public sealed partial class EngineReader
{
    private const int EntityItemBudget = 32 * 1024;
    private const int EntitiesBudget = 1024 * 1024;
    private sealed record EntitySize(long Json, long Prefix, long Label);

    /// <summary>One bounded snapshot, grouped SQL reads and a continuation scoped to the same references and connection.</summary>
    public EngineEntitiesPage Entities(IReadOnlyList<EngineEntityRef> refs, string? after,
        CancellationToken cancellationToken, string? source = null, string connectionScope = "",
        Func<EngineEntitiesPage, int>? measure = null)
    {
        if (refs is null || refs.Count > 200 || refs.Any(item => item is null ||
            string.IsNullOrWhiteSpace(item.Kind) || string.IsNullOrWhiteSpace(item.Id)))
            throw new ArgumentException("bad_args");
        foreach (var item in refs) _ = DisplayQueries.Json(item.Kind);
        var ordered = refs.Distinct().ToArray();
        using var read = db.OpenSnapshot(cancellationToken);
        var selected = source ?? machine;
        ReadMeta(read, selected);
        var mirrored = selected != machine;
        var generation = mirrored ? read.QueryFirst("SELECT generation FROM engine_mirror WHERE machine=$machine AND state<>'retired'",
            row => row.Long(0), new SqlArg("$machine", selected)) : 0;
        var scope = Digest(writer =>
        {
            writer.WriteStringValue(selected); writer.WriteNumberValue(generation); writer.WriteStringValue(connectionScope);
        });
        var references = Digest(writer =>
        {
            foreach (var item in ordered) { writer.WriteStringValue(item.Kind); writer.WriteStringValue(item.Id); }
        });
        var start = Offset(after, scope, references, ordered.Length);
        string Cursor(int offset) => $"1/{offset.ToString(CultureInfo.InvariantCulture)}/{scope}/{references}";

        // Inspect sizes in SQL first. Large stored values never become an unbounded managed JSON string.
        var sizes = new Dictionary<EngineEntityRef, EntitySize>();
        foreach (var group in ordered.Skip(start).GroupBy(item => item.Kind))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mirrored && !DisplayQueries.Kinds("mirror").Contains(group.Key)) continue;
            foreach (var item in ReadSizes(read, group.Key, group.Select(item => item.Id).ToArray(), selected, mirrored))
                sizes.Add(new(group.Key, item.Id), item.Size);
        }
        var stop = start;
        var reserved = 1024; // Empty result envelope and the fixed-length continuation, including the socket reply id.
        for (; stop < ordered.Length; stop++)
        {
            if (!sizes.TryGetValue(ordered[stop], out var size)) continue;
            if (size.Json > EntityItemBudget) throw new InvalidOperationException("Display item exceeds the 32 KiB UTF-8 budget.");
            // JSON escaping is at most six bytes per input byte. Existing display prefixes add bounded metadata.
            var upper = (int)Math.Min(EntityItemBudget, 6 * (size.Json + size.Prefix + size.Label) + 1024) + 1;
            if (reserved + upper > EntitiesBudget) break;
            reserved += upper;
        }
        var rows = new Dictionary<EngineEntityRef, JsonElement>();
        foreach (var group in ordered.Skip(start).Take(stop - start).Where(sizes.ContainsKey).GroupBy(item => item.Kind))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in ReadEntities(read, group.Key, group.Select(item => item.Id).ToArray(), selected, mirrored))
                rows.Add(new(group.Key, item.Id), item.Item);
        }
        var result = new EngineEntitiesPage(ordered.Skip(start).Take(stop - start).Where(rows.ContainsKey)
            .Select(item => rows[item]).ToArray(), stop < ordered.Length ? Cursor(stop) : null);
        if ((measure?.Invoke(result) ?? PageBytes(result)) > EntitiesBudget)
            throw new InvalidOperationException("Entity response exceeds the 1 MiB UTF-8 budget.");
        return result;
    }

    private static IReadOnlyList<(string Id, EntitySize Size)> ReadSizes(IReadScope read, string kind,
        string[] ids, string selected, bool mirrored)
    {
        var sql = mirrored ? """
            SELECT r.entity_id,length(CAST(r.row_json AS BLOB)),0,0
            FROM engine_mirror_row r JOIN engine_mirror m ON m.machine=r.machine AND m.generation=r.generation
            WHERE r.machine=$machine AND r.entity_kind=$kind AND m.state<>'retired'
                AND r.entity_id IN (SELECT value FROM json_each($ids))
            """ : $"""
            WITH bounded AS (SELECT t.id,{DisplayQueries.Json(kind)} AS item,{DisplayQueries.PrefixColumns(kind)}
                FROM {DisplayQueries.Source(kind)} t WHERE t.machine=$machine AND t.id IN (SELECT value FROM json_each($ids)))
            SELECT id,length(CAST(item AS BLOB)),COALESCE(length(prefix),0),COALESCE(length(label_prefix),0) FROM bounded
            """;
        return read.Query(sql, row => (row.Text(0), new EntitySize(row.Long(1), row.Long(2), row.Long(3))),
            new("$machine", selected), new("$kind", kind), new("$ids", JsonSerializer.Serialize(ids, EngineJson.Default.IReadOnlyListString)));
    }

    private static IReadOnlyList<(string Id, JsonElement Item)> ReadEntities(IReadScope read, string kind,
        string[] ids, string selected, bool mirrored)
    {
        var sql = mirrored ? """
            SELECT r.entity_id,CASE WHEN length(CAST(r.row_json AS BLOB))<=32768 THEN r.row_json END,NULL,NULL,NULL,NULL
            FROM engine_mirror_row r JOIN engine_mirror m ON m.machine=r.machine AND m.generation=r.generation
            WHERE r.machine=$machine AND r.entity_kind=$kind AND m.state<>'retired'
                AND r.entity_id IN (SELECT value FROM json_each($ids))
            """ : $"""
            WITH bounded AS (SELECT t.id,{DisplayQueries.Json(kind)} AS item,{DisplayQueries.PrefixColumns(kind)}
                FROM {DisplayQueries.Source(kind)} t WHERE t.machine=$machine AND t.id IN (SELECT value FROM json_each($ids)))
            SELECT id,CASE WHEN length(CAST(item AS BLOB))<=32768 THEN item END,prefix,total,label_prefix,label_total FROM bounded
            """;
        return read.Query(sql, row =>
        {
            if (row.IsNull(1)) throw new InvalidOperationException("Display item exceeds the 32 KiB UTF-8 budget.");
            var item = DisplayQueries.Project(row, 1);
            // The SQL base JSON is not the wire item: prefixes, metadata and escaping are projected afterward.
            if (JsonSerializer.SerializeToUtf8Bytes(item, EngineJson.Default.JsonElement).Length > EntityItemBudget)
                throw new InvalidOperationException("Display item exceeds the 32 KiB UTF-8 budget.");
            return (row.Text(0), item);
        }, new("$machine", selected), new("$kind", kind),
            new("$ids", JsonSerializer.Serialize(ids, EngineJson.Default.IReadOnlyListString)));
    }

    private static int Offset(string? after, string scope, string references, int count)
    {
        if (after is null) return 0;
        var parts = after.Split('/');
        if (parts.Length != 4 || parts[0] != "1" || parts[2] != scope || parts[3] != references ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 1 || offset >= count)
            throw new ArgumentException("invalid_cursor");
        return offset;
    }

    private static string Digest(Action<Utf8JsonWriter> write)
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes)) { writer.WriteStartArray(); write(writer); writer.WriteEndArray(); }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.WrittenSpan));
    }

    private static int PageBytes(EngineEntitiesPage page)
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteStartArray("items");
            foreach (var item in page.Items) item.WriteTo(writer);
            writer.WriteEndArray();
            if (page.Next is not null) writer.WriteString("next", page.Next);
            writer.WriteEndObject();
        }
        return bytes.WrittenCount;
    }
}
