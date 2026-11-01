using Assembly.Engine.Execution;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Reading;
using Assembly.Infrastructure.Socket;

namespace Assembly.Infrastructure.Engine;

public interface IEngineSubscription : ISocketSubscription
{
    void Acknowledge(long through);
    void Next();
}

/// <summary>Durable catch-up followed by buffered publisher events on the same global cursor.</summary>
public sealed class EngineSocketStream(EngineRuntime runtime)
{
    public IEngineSubscription Subscribe(long id, string topic, string? key, long? after,
        Action<string> send, CancellationToken cancellationToken, CursorAt? at = null, string? machine = null) =>
        Subscribe(id, topic, key, after, (frame, _) => send(frame), cancellationToken, () => { }, at, machine);

    internal IEngineSubscription Subscribe(long id, string topic, string? key, long? after,
        Action<string, ISocketSubscription?> send, CancellationToken cancellationToken, Action overflow, CursorAt? at = null, string? machine = null)
    {
        machine ??= runtime.Machine;
        var high = runtime.Reader.HighWatermark(cancellationToken, machine);
        if (topic is not ("runs" or "sessions" or "needs_me" or "messages" or "prs" or "usage" or "mirror"))
            throw new ArgumentException("Unknown engine topic");
        if (key is not null && (topic == "mirror" || string.IsNullOrWhiteSpace(key))) throw new ArgumentException("Invalid engine key");
        if (after is { } cursor && (cursor < 0 || cursor > high))
            throw new ArgumentException("Invalid engine cursor");
        if (after is { } fingerprintCursor && at is not null)
        {
            var actual = runtime.Reader.EventAt(fingerprintCursor, cancellationToken, machine);
            if (actual is null && fingerprintCursor > 0) after = null; // compacted cursors re-snapshot
            else if (actual != at) throw new ArgumentException("Invalid engine cursor");
        }
        return new Subscription(runtime, id, topic, key, after, send, cancellationToken, overflow, machine);
    }

    private sealed class Subscription : IEngineSubscription
    {
        private const int MaxBuffered = 16 * 1024 * 1024;
        private readonly EngineRuntime _runtime;
        private readonly string _machine;
        private readonly EnginePublisher _publisher;
        private readonly long _id;
        private readonly string _topic;
        private readonly string? _key;
        private readonly string _readerId = Guid.NewGuid().ToString("N");
        private readonly Action<string, ISocketSubscription?> _send;
        private readonly Action _overflow;
        private readonly CancellationTokenSource _closed;
        private readonly Channel<(EngineEvent? Event, int Bytes, int Version)> _pending = Channel.CreateUnbounded<(EngineEvent?, int, int)>(new() { SingleReader = true });
        private readonly Lock _cursor = new();
        private long _through;
        private long _ack;
        private long _buffered;
        private int _disposed;
        private int _snapshotVersion;
        private int _activeVersion;
        private TaskCompletionSource? _next;
        private IDisposable? _reader;

        public CancellationToken Closed { get; }
        public Task Completion { get; }

        public Subscription(EngineRuntime runtime, long id, string topic, string? key, long? after,
            Action<string, ISocketSubscription?> send, CancellationToken cancellationToken, Action overflow, string machine)
        {
            _runtime = runtime;
            _machine = machine;
            _publisher = runtime.PublisherFor(machine);
            _id = id;
            _topic = topic;
            _key = key;
            _send = send;
            _overflow = overflow;
            _closed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runtime.Stopping);
            Closed = _closed.Token;
            _ack = after ?? 0;
            var registration = _publisher.Register(Enqueue, out var cut, Reset);
            Completion = Task.Run(() => RunAsync(after, cut, registration));
        }

        private void Reset()
        {
            Interlocked.Increment(ref _snapshotVersion);
            _pending.Writer.TryWrite((null, 0, Volatile.Read(ref _snapshotVersion)));
            lock (_cursor)
            {
                _next?.TrySetResult();
                _next = null;
            }
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
            _pending.Writer.TryWrite((item, bytes, Volatile.Read(ref _snapshotVersion)));
        }

        private async Task RunAsync(long? after, long cut, IDisposable registration)
        {
            try
            {
                if (after is { } cursor)
                {
                    _through = cursor;
                    _reader = Track(cursor);
                    await ReplayAsync(Math.Max(cut, cursor), _activeVersion);
                }
                else await SnapshotAsync();
                await SynchronizeAsync();
                await foreach (var (item, bytes, version) in _pending.Reader.ReadAllAsync(Closed))
                {
                    Interlocked.Add(ref _buffered, -bytes);
                    // Reset takes priority over the FIFO, including events queued before its wake marker.
                    await SynchronizeAsync();
                    if (item is null || version != _activeVersion) continue;
                    if (item.Seq <= _through) continue;
                    // A publisher cursor is contiguous; a broken cursor must reload current state.
                    if (item.Seq != _through + 1) { await SnapshotAsync(); continue; }
                    if (_topic == "mirror") await ReplayAsync(item.Seq, version);
                    else Events(_through, item.Seq, EngineReader.Matches(item, _topic, _key) ? [item] : [], false,
                        null, new(item.Kind, item.EntityId, item.Version), version);
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
                _reader?.Dispose();
                _closed.Dispose();
            }
        }

        private IDisposable Track(long through) => _publisher.TrackReader(_readerId, through, () =>
        {
            _closed.Cancel();
            _overflow();
        });

        private async Task SynchronizeAsync()
        {
            while (_activeVersion != Volatile.Read(ref _snapshotVersion))
                await SnapshotAsync();
        }

        private async Task ReplayAsync(long cut, int version)
        {
            do
            {
                Closed.ThrowIfCancellationRequested();
                if (version != Volatile.Read(ref _snapshotVersion)) return;
                StreamPage page;
                try { page = _runtime.Reader.Since(_topic, _key, _through, Closed, cut, _machine); }
                catch (ArgumentException error) when (_machine != _runtime.Machine && error.Message == "cursor_ahead" && version != Volatile.Read(ref _snapshotVersion))
                {
                    // Promotion may rewind the database while an old replay is reading it.
                    return;
                }
                if (version != Volatile.Read(ref _snapshotVersion)) return;
                if (page.SnapshotRequired) { await SnapshotAsync(); return; }
                if (!Events(page.After, page.Through, page.Events, page.HasMore, page.Rows, page.At, version)) return;
            } while (_through < cut);
        }

        private async Task SnapshotAsync()
        {
            // A new snapshot needs only events after its cut, even when replay had an older cursor.
            _reader?.Dispose();
            _reader = null;
            var version = Volatile.Read(ref _snapshotVersion);
            using var snapshot = _runtime.Reader.Snapshot(_topic, _key, Closed, _machine);
            _reader = Track(snapshot.Through);
            bool more;
            do
            {
                Closed.ThrowIfCancellationRequested();
                if (version != Volatile.Read(ref _snapshotVersion)) return;
                var page = snapshot.ReadNext();
                more = page.HasMore;
                var frame = JsonSerializer.Serialize(new EngineSnapshotFrame(_id, _machine, page.Through, page.Items, more, page.At),
                    EngineSocketJson.Default.EngineSnapshotFrame);
                Task? next = null;
                lock (_cursor)
                {
                    if (version != Volatile.Read(ref _snapshotVersion)) return;
                    if (more && _topic == "mirror")
                    {
                        _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        next = _next.Task;
                    }
                    Send(frame);
                    if (version != Volatile.Read(ref _snapshotVersion)) return;
                    // A partial snapshot cannot be acknowledged as complete.
                    if (!more) { _through = page.Through; _ack = page.Through; _activeVersion = version; }
                }
                if (next is not null) await next.WaitAsync(Closed);
            } while (more);
        }

        private bool Events(long after, long through, IReadOnlyList<EngineEvent> events, bool more,
            IReadOnlyList<JsonElement>? rows, CursorAt? at, int version)
        {
            var frame = JsonSerializer.Serialize(new EngineEventsFrame(_id, _machine, after, through, events, more, rows, at),
                EngineSocketJson.Default.EngineEventsFrame);
            lock (_cursor)
            {
                if (version != Volatile.Read(ref _snapshotVersion)) return false;
                Send(frame);
                if (version != Volatile.Read(ref _snapshotVersion)) return false;
                _through = through;
                return true;
            }
        }

        private void Send(string frame)
        {
            if (Encoding.UTF8.GetByteCount(frame) > SocketConnection.MaxFrame)
                throw new InvalidOperationException("Engine frame exceeds the socket budget");
            _send(frame, this);
        }

        public void Next()
        {
            lock (_cursor)
            {
                if (Closed.IsCancellationRequested || _topic != "mirror" || _next is null)
                    throw new ArgumentException("No pending mirror snapshot");
                var next = _next;
                _next = null;
                next.TrySetResult();
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
            _publisher.Acknowledge(_readerId, through);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _closed.Cancel();
            _pending.Writer.TryComplete();
        }
    }
}
