using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Infrastructure.Engine;

/// <summary>One committed-log cursor. Consumers must only enqueue, throwing if their queue overflows.</summary>
public sealed class EnginePublisher(IAppDatabase db)
{
    private readonly Lock _gate = new();
    private readonly List<Consumer> _consumers = [];
    private readonly Dictionary<string, Reader> _readers = [];
    private long _published;
    private sealed class Consumer(Action<EngineEvent> onEvent)
    {
        public Action<EngineEvent> OnEvent { get; } = onEvent;
    }

    private sealed class Reader(long through, Action reset)
    {
        public long Through { get; set; } = through;
        public Action Reset { get; } = reset;
    }

    public long PublishedThrough { get { lock (_gate) return _published; } }
    public long MinimumReaderAck { get { lock (_gate) return _readers.Count == 0 ? _published : _readers.Values.Min(r => r.Through); } }

    public IDisposable Register(Action<EngineEvent> onEvent, out long publishedThrough)
    {
        var consumer = new Consumer(onEvent);
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
        lock (_gate)
            if (_readers.TryGetValue(id, out var reader))
                reader.Through = Math.Max(reader.Through, Math.Min(through, _published));
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

    internal void Drain(CancellationToken cancellationToken)
    {
        while (true)
        {
            var events = db.Query("""
                SELECT machine,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json
                FROM engine_event WHERE seq>$after ORDER BY seq LIMIT 100
                """, r => new EngineEvent(r.Text(0), r.Long(1), r.Text(2), r.Text(3), r.Text(4), r.Long(5), r.Long(6),
                    r.Long(7) != 0, JsonSerializer.Deserialize(r.Text(8), EngineJson.Default.EngineEventData)!),
                cancellationToken, new SqlArg("$after", PublishedThrough));
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
