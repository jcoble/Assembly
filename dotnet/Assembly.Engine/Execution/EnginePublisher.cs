using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Execution;

/// <summary>One committed-log cursor. Consumers must only enqueue, throwing if their queue overflows.</summary>
public sealed class EnginePublisher(IAppDatabase db, string? machine = null, long publishedThrough = 0, Action? acknowledged = null, long? generation = null)
{
    private readonly Lock _gate = new();
    private readonly List<Consumer> _consumers = [];
    private readonly Dictionary<string, Reader> _readers = [];
    private long _published = publishedThrough;
    private long? _generation = generation;
    private sealed class Consumer(Action<EngineEvent> onEvent, Action? reset)
    {
        public Action<EngineEvent> OnEvent { get; } = onEvent;
        public Action? Reset { get; } = reset;
    }

    private sealed class Reader(long through, Action reset)
    {
        public long Through { get; set; } = through;
        public Action Reset { get; } = reset;
    }

    public long PublishedThrough { get { lock (_gate) return _published; } }
    public long MinimumReaderAck { get { lock (_gate) return _readers.Count == 0 ? _published : _readers.Values.Min(r => r.Through); } }

    public IDisposable Register(Action<EngineEvent> onEvent, out long publishedThrough, Action? reset = null)
    {
        var consumer = new Consumer(onEvent, reset);
        lock (_gate)
        {
            _consumers.Add(consumer);
            publishedThrough = _published;
        }
        return new Registration(() => { lock (_gate) _consumers.Remove(consumer); });
    }

    public IDisposable TrackReader(string id, long through, Action reset)
    {
        var reader = new Reader(through, reset);
        lock (_gate)
        {
            if (!_readers.TryAdd(id, reader)) throw new ArgumentException("Reader already registered", nameof(id));
        }
        return new Registration(() =>
        {
            lock (_gate)
                if (_readers.TryGetValue(id, out var current) && ReferenceEquals(current, reader)) _readers.Remove(id);
        });
    }

    public void Acknowledge(string id, long through)
    {
        bool changed = false;
        lock (_gate)
            if (_readers.TryGetValue(id, out var reader))
            {
                var next = Math.Max(reader.Through, Math.Min(through, _published));
                changed = next != reader.Through;
                reader.Through = next;
            }
        if (changed) acknowledged?.Invoke();
    }

    public void InvalidateReadersBefore(long through)
    {
        List<Action> resets;
        lock (_gate)
        {
            var stale = _readers.Where(r => r.Value.Through < through).ToArray();
            resets = stale.Select(r => r.Value.Reset).ToList();
            foreach (var reader in stale) _readers.Remove(reader.Key);
        }
        foreach (var reset in resets)
            try { reset(); } catch { /* A failed reader is already removed. */ }
    }

    internal void Reset(long through, long? generation = null)
    {
        lock (_gate)
        {
            _published = through;
            _generation = generation;
            _readers.Clear();
            foreach (var consumer in _consumers.ToArray())
                try { consumer.Reset?.Invoke(); } catch { _consumers.Remove(consumer); }
        }
    }

    internal void Drain(CancellationToken cancellationToken)
    {
        while (true)
        {
            var events = db.Query("""
                SELECT machine,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json
                FROM engine_event WHERE machine=COALESCE($machine,(SELECT machine_id FROM engine_meta WHERE singleton=1))
                    AND seq>$after AND ($generation IS NULL OR EXISTS(
                        SELECT 1 FROM engine_mirror m WHERE m.machine=$machine AND m.generation=$generation AND m.state='live'))
                    ORDER BY seq LIMIT 100
                """, r => new EngineEvent(r.Text(0), r.Long(1), r.Text(2), r.Text(3), r.Text(4), r.Long(5), r.Long(6),
                    r.Long(7) != 0, JsonSerializer.Deserialize(r.Text(8), EngineJson.Default.EngineEventData)!),
                cancellationToken, new SqlArg("$after", PublishedThrough), new SqlArg("$machine", machine), new SqlArg("$generation", _generation));
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    foreach (var consumer in _consumers.ToArray())
                        try { consumer.OnEvent(item); } catch { _consumers.Remove(consumer); }
                    _published = item.Seq;
                }
            }
            if (events.Count < 100) return;
        }
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
