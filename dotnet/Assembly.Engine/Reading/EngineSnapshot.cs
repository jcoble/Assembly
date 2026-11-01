using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Reading;

/// <summary>Pages current tables in one read transaction, ordered by kind and ID.</summary>
public sealed class EngineSnapshot : IDisposable
{
    private readonly IReadSnapshot _read;
    private readonly string _sql;
    private readonly string? _key;
    private readonly string _machine;
    private string _lastKind = "";
    private string _lastId = "";
    private bool _disposed;
    public long Through { get; }
    public CursorAt? At { get; }
    private readonly int _budget;

    internal EngineSnapshot(IReadSnapshot read, long through, string topic, string? key, string[] kinds, string machine)
    {
        _read = read;
        Through = through;
        At = EngineReader.EventAt(read, through, machine);
        _budget = topic == "mirror" ? 1024 * 1024 : 64 * 1024;
        _key = key;
        _machine = machine;
        var selects = kinds.Select(kind => $"""
            SELECT '{kind}' AS kind,t.id AS id,{(topic == "mirror" ? DisplayQueries.MirrorJson(kind) : DisplayQueries.Json(kind))} AS item
            FROM engine_{kind} t WHERE t.machine=$machine AND ($key IS NULL OR {DisplayQueries.KeyPredicate(topic, kind)})
            """);
        _sql = $"""
            SELECT kind,id,item FROM ({string.Join(" UNION ALL ", selects)})
            WHERE kind>$kind OR (kind=$kind AND id>$id) ORDER BY kind,id LIMIT 201
            """;
    }

    public SnapshotPage ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var rows = _read.Query(_sql, r => (Kind: r.Text(0), Id: r.Text(1), Json: r.Text(2)),
                new("$machine", _machine), new("$key", _key), new("$kind", _lastKind), new("$id", _lastId));
            List<JsonElement> items = [];
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new SnapshotPage(Through, [], false, At),
                EngineJson.Default.SnapshotPage).Length;
            foreach (var row in rows)
            {
                if (items.Count == 200) break;
                var item = DisplayQueries.Parse(row.Json);
                var size = JsonSerializer.SerializeToUtf8Bytes(item, EngineJson.Default.JsonElement).Length;
                if (items.Count > 0 && bytes + size + 1 > _budget) break;
                bytes += size + 1;
                items.Add(item);
                _lastKind = row.Kind;
                _lastId = row.Id;
            }
            var more = items.Count < rows.Count;
            if (!more) Dispose();
            return new(Through, items, more, At);
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _read.Dispose();
    }
}
