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
        : this(read, through, topic, key, machine, LocalQuery(topic, kinds)) { }

    internal EngineSnapshot(IReadSnapshot read, long through, string topic, string? key, string machine, string sql)
    {
        _read = read;
        Through = through;
        At = EngineReader.EventAt(read, through, machine);
        _budget = topic == "mirror" ? 1024 * 1024 : 64 * 1024;
        _key = key;
        _machine = machine;
        _sql = $"""
            SELECT kind,id,item,prefix,total,label_prefix,label_total FROM ({sql})
            WHERE kind>$kind OR (kind=$kind AND id>$id) ORDER BY kind,id LIMIT 201
            """;
    }

    private static string LocalQuery(string topic, string[] kinds)
    {
        var selects = kinds.Select(kind => $"""
            SELECT '{kind}' AS kind,t.id AS id,{(topic == "mirror" ? DisplayQueries.MirrorJson(kind) : DisplayQueries.Json(kind))} AS item,{DisplayQueries.PrefixColumns(kind)}
            FROM engine_{kind} t WHERE t.machine=$machine AND ($key IS NULL OR {DisplayQueries.KeyPredicate(topic, kind)})
            """);
        return string.Join(" UNION ALL ", selects);
    }

    public SnapshotPage ReadNext(Func<SnapshotPage, int>? measureCandidate = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var rows = _read.Query(_sql, r => (Kind: r.Text(0), Id: r.Text(1), Item: DisplayQueries.Project(r, 2)),
                new("$machine", _machine), new("$key", _key), new("$kind", _lastKind), new("$id", _lastId));
            int Measure(SnapshotPage page) => measureCandidate?.Invoke(page) ?? JsonSerializer.SerializeToUtf8Bytes(page, EngineJson.Default.SnapshotPage).Length;
            SnapshotPage Candidate(int count) => new(Through, rows.Take(count).Select(row => row.Item).ToArray(), count < rows.Count, At);
            SnapshotPage? selected = null;
            if (rows.Count == 0)
            {
                selected = Candidate(0);
                if (Measure(selected) > _budget) throw new InvalidOperationException("Snapshot frame exceeds budget.");
            }
            else
            {
                var low = 0; var high = Math.Min(200, rows.Count);
                while (low < high)
                {
                    var count = low + (high - low + 1) / 2;
                    var candidate = Candidate(count);
                    if (Measure(candidate) <= _budget) { low = count; selected = candidate; }
                    else high = count - 1;
                }
                if (selected is null) throw new InvalidOperationException("Display item exceeds snapshot frame budget.");
                var last = rows[selected.Items.Count - 1];
                _lastKind = last.Kind;
                _lastId = last.Id;
            }
            if (!selected.HasMore) Dispose();
            return selected;
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
