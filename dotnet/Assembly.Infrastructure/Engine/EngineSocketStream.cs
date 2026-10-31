using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Reading;
using Assembly.Infrastructure.Socket;

namespace Assembly.Infrastructure.Engine;

public interface IEngineSubscription : ISocketSubscription
{
    void Acknowledge(long through);
}

/// <summary>Durable catch-up followed by buffered publisher events on the same global cursor.</summary>
public sealed class EngineSocketStream(EngineRuntime runtime)
{
    public IEngineSubscription Subscribe(long id, string topic, string? key, long? after,
        Action<string> send, CancellationToken cancellationToken) =>
        Subscribe(id, topic, key, after, (frame, _) => send(frame), cancellationToken, () => { });

    internal IEngineSubscription Subscribe(long id, string topic, string? key, long? after,
        Action<string, ISocketSubscription?> send, CancellationToken cancellationToken, Action overflow)
    {
        if (topic is not ("runs" or "sessions" or "needs_me" or "messages" or "prs" or "usage"))
            throw new ArgumentException("Unknown engine topic");
        if (key is not null && string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Invalid engine key");
        if (after is { } cursor && (cursor < 0 || cursor > runtime.Reader.HighWatermark(cancellationToken)))
            throw new ArgumentException("Invalid engine cursor");
        return new Subscription(runtime, id, topic, key, after, send, cancellationToken, overflow);
    }

    private sealed class Subscription : IEngineSubscription
    {
        private const int MaxBuffered = 16 * 1024 * 1024;
        private readonly EngineRuntime _runtime;
        private readonly long _id;
        private readonly string _topic;
        private readonly string? _key;
        private readonly string _readerId = Guid.NewGuid().ToString("N");
        private readonly Action<string, ISocketSubscription?> _send;
        private readonly Action _overflow;
        private readonly CancellationTokenSource _closed;
        private readonly Channel<(EngineEvent Event, int Bytes)> _pending = Channel.CreateUnbounded<(EngineEvent, int)>(new() { SingleReader = true });
        private readonly Lock _cursor = new();
        private long _through;
        private long _ack;
        private long _buffered;
        private int _disposed;

        public CancellationToken Closed { get; }
        public Task Completion { get; }

        public Subscription(EngineRuntime runtime, long id, string topic, string? key, long? after,
            Action<string, ISocketSubscription?> send, CancellationToken cancellationToken, Action overflow)
        {
            _runtime = runtime;
            _id = id;
            _topic = topic;
            _key = key;
            _send = send;
            _overflow = overflow;
            _closed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Closed = _closed.Token;
            _ack = after ?? 0;
            var registration = runtime.Publisher.Register(Enqueue, out var cut);
            Completion = Task.Run(() => RunAsync(after, cut, registration));
        }

        private void Enqueue(EngineEvent item)
        {
            if (Closed.IsCancellationRequested) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(item, EngineJson.Default.EngineEvent).Length;
            if (Interlocked.Add(ref _buffered, bytes) > MaxBuffered)
            {
                // Called under the publisher lock: cancellation and closing never wait for this consumer.
                _closed.Cancel();
                _overflow();
                return;
            }
            _pending.Writer.TryWrite((item, bytes));
        }

        private async Task RunAsync(long? after, long cut, IDisposable registration)
        {
            IDisposable? reader = null;
            try
            {
                if (after is { } cursor)
                {
                    _through = cursor;
                    reader = Track(cursor);
                    Replay(Math.Max(cut, cursor));
                }
                else Snapshot();
                reader ??= Track(_through);
                await foreach (var (item, bytes) in _pending.Reader.ReadAllAsync(Closed))
                {
                    Interlocked.Add(ref _buffered, -bytes);
                    if (item.Seq <= _through) continue;
                    // A publisher cursor is contiguous; a broken cursor must reload current state.
                    if (item.Seq != _through + 1) { Snapshot(); continue; }
                    Events(_through, item.Seq, EngineReader.Matches(item, _topic, _key) ? [item] : [], false);
                }
            }
            catch (OperationCanceledException) when (Closed.IsCancellationRequested) { }
            catch (Exception)
            {
                _send(Encoding.UTF8.GetString(SocketDispatcher.Frame(new ErrorFrame(_id, "failed", "Engine subscription requires a new snapshot"),
                    SocketJson.Default.ErrorFrame).Span), null);
            }
            finally
            {
                Dispose();
                registration.Dispose();
                reader?.Dispose();
                _closed.Dispose();
            }
        }

        private IDisposable Track(long through) => _runtime.Publisher.TrackReader(_readerId, through, () =>
        {
            _closed.Cancel();
            _overflow();
        });

        private void Replay(long cut)
        {
            do
            {
                Closed.ThrowIfCancellationRequested();
                var page = _runtime.Reader.Since(_topic, _key, _through, Closed, cut);
                if (page.SnapshotRequired) { Snapshot(); return; }
                Events(page.After, page.Through, page.Events, page.HasMore);
            } while (_through < cut);
        }

        private void Snapshot()
        {
            using var snapshot = _runtime.Reader.Snapshot(_topic, _key, Closed);
            bool more;
            do
            {
                Closed.ThrowIfCancellationRequested();
                var page = snapshot.ReadNext();
                more = page.HasMore;
                var frame = JsonSerializer.Serialize(new EngineSnapshotFrame(_id, _runtime.Machine, page.Through, page.Items, more),
                    EngineSocketJson.Default.EngineSnapshotFrame);
                lock (_cursor)
                {
                    _send(frame, this);
                    // A partial snapshot cannot be acknowledged as complete.
                    if (!more) _through = page.Through;
                }
            } while (more);
        }

        private void Events(long after, long through, IReadOnlyList<EngineEvent> events, bool more)
        {
            var frame = JsonSerializer.Serialize(new EngineEventsFrame(_id, _runtime.Machine, after, through, events, more),
                EngineSocketJson.Default.EngineEventsFrame);
            lock (_cursor)
            {
                _send(frame, this);
                _through = through;
            }
        }

        public void Acknowledge(long through)
        {
            lock (_cursor)
            {
                if (Closed.IsCancellationRequested || through < _ack || through > _through)
                    throw new ArgumentException("Invalid engine acknowledgement");
                _ack = through;
            }
            _runtime.Publisher.Acknowledge(_readerId, through);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _closed.Cancel();
            _pending.Writer.TryComplete();
        }
    }
}
