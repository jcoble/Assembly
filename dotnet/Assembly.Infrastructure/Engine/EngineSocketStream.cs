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
        Action<string> send, CancellationToken cancellationToken, CursorAt? at = null) =>
        Subscribe(id, topic, key, after, (frame, _) => send(frame), cancellationToken, () => { }, at);

    internal IEngineSubscription Subscribe(long id, string topic, string? key, long? after,
        Action<string, ISocketSubscription?> send, CancellationToken cancellationToken, Action overflow, CursorAt? at = null)
    {
        if (topic is not ("runs" or "sessions" or "needs_me" or "messages" or "prs" or "usage" or "mirror"))
            throw new ArgumentException("Unknown engine topic");
        if (key is not null && (topic == "mirror" || string.IsNullOrWhiteSpace(key))) throw new ArgumentException("Invalid engine key");
        if (after is { } cursor && (cursor < 0 || cursor > runtime.Reader.HighWatermark(cancellationToken)))
            throw new ArgumentException("Invalid engine cursor");
        if (after is { } fingerprintCursor && at is not null)
        {
            var actual = runtime.Reader.EventAt(fingerprintCursor, cancellationToken);
            if (actual is null && fingerprintCursor > 0) after = null; // compacted cursors re-snapshot
            else if (actual != at) throw new ArgumentException("Invalid engine cursor");
        }
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
        private TaskCompletionSource? _next;

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
                    await ReplayAsync(Math.Max(cut, cursor));
                }
                else
                {
                    reader = Track(0);
                    await SnapshotAsync();
                }
                await foreach (var (item, bytes) in _pending.Reader.ReadAllAsync(Closed))
                {
                    Interlocked.Add(ref _buffered, -bytes);
                    if (item.Seq <= _through) continue;
                    // A publisher cursor is contiguous; a broken cursor must reload current state.
                    if (item.Seq != _through + 1) { await SnapshotAsync(); continue; }
                    if (_topic == "mirror") await ReplayAsync(item.Seq);
                    else Events(_through, item.Seq, EngineReader.Matches(item, _topic, _key) ? [item] : [], false,
                        null, new(item.Kind, item.EntityId, item.Version));
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

        private async Task ReplayAsync(long cut)
        {
            do
            {
                Closed.ThrowIfCancellationRequested();
                var page = _runtime.Reader.Since(_topic, _key, _through, Closed, cut);
                if (page.SnapshotRequired) { await SnapshotAsync(); return; }
                Events(page.After, page.Through, page.Events, page.HasMore, page.Rows, page.At);
            } while (_through < cut);
        }

        private async Task SnapshotAsync()
        {
            using var snapshot = _runtime.Reader.Snapshot(_topic, _key, Closed);
            bool more;
            do
            {
                Closed.ThrowIfCancellationRequested();
                var page = snapshot.ReadNext();
                more = page.HasMore;
                var frame = JsonSerializer.Serialize(new EngineSnapshotFrame(_id, _runtime.Machine, page.Through, page.Items, more, page.At),
                    EngineSocketJson.Default.EngineSnapshotFrame);
                Task? next = null;
                lock (_cursor)
                {
                    if (more && _topic == "mirror")
                    {
                        _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        next = _next.Task;
                    }
                    Send(frame);
                    // A partial snapshot cannot be acknowledged as complete.
                    if (!more) _through = page.Through;
                }
                if (next is not null) await next.WaitAsync(Closed);
            } while (more);
        }

        private void Events(long after, long through, IReadOnlyList<EngineEvent> events, bool more,
            IReadOnlyList<JsonElement>? rows, CursorAt? at)
        {
            var frame = JsonSerializer.Serialize(new EngineEventsFrame(_id, _runtime.Machine, after, through, events, more, rows, at),
                EngineSocketJson.Default.EngineEventsFrame);
            lock (_cursor)
            {
                Send(frame);
                _through = through;
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
