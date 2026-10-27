using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Infrastructure.Socket;

/// <summary>
/// One page's socket (plan 5.1): its <c>hello</c> first, then one receive loop, one sender, and one write queue that
/// runs its writes one at a time in arrival order. A frame over 4 MB, or more than 16 MB waiting to be sent, closes
/// it. Closing cancels its reads; writes already queued still commit.
/// </summary>
internal sealed class SocketConnection(WebSocket socket, SocketDispatcher calls, TimeProvider time)
{
    internal const int Protocol = 1;
    internal const int MaxFrame = 4 * 1024 * 1024;
    private const long MaxWaiting = 16 * 1024 * 1024;
    private static long _last;

    private readonly CancellationTokenSource _closed = new();
    private readonly Channel<(ReadOnlyMemory<byte> Frame, TopicSet.Subscription? From)> _outbox =
        Channel.CreateUnbounded<(ReadOnlyMemory<byte>, TopicSet.Subscription?)>(new() { SingleReader = true });
    private readonly Channel<Func<Task>> _writes = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    // The last write queued under each order key, until it ends. Only the receive loop adds one.
    private readonly Dictionary<string, Task> _ordered = [];
    // Each running call's cancel, by its id.
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    // This socket's subscriptions, by their sub id. Only the receive loop, and then its finally, touch it.
    private readonly Dictionary<long, TopicSet.Subscription> _subscriptions = [];
    private long _waiting;
    // Why the socket closes, once the receive loop ends; closed after the last answer is sent, since a socket can't
    // send two things at once.
    private (WebSocketCloseStatus Status, string Reason)? _closing;

    public string Id { get; } = $"socket-{Interlocked.Increment(ref _last)}";

    /// <summary>Fires as the socket closes; a read's token is linked to it, a write's isn't.</summary>
    public CancellationToken Closed => _closed.Token;

    public async Task RunAsync(byte[] token, string machine, CancellationToken stopping)
    {
        if (!await HelloAsync(token, machine, stopping))
            return;

        var sending = SendLoopAsync();
        var writing = WriteLoopAsync();
        try
        {
            while (await ReceiveAsync(stopping) is { } text)
                if (!await HandleAsync(text))
                    break;
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        {
            // The page went away, or the host is stopping.
        }
        finally
        {
            foreach (var subscription in _subscriptions.Values)
                calls.Unsubscribe(subscription);
            _subscriptions.Clear();
            await _closed.CancelAsync();
            _writes.Writer.TryComplete();
            await writing;
            Task[] ordered;
            lock (_ordered)
                ordered = [.. _ordered.Values];
            await Task.WhenAll(ordered.Select(write => write.ContinueWith(_ => { }, TaskScheduler.Default)));
            calls.ConnectionClosed(Id);
            _outbox.Writer.TryComplete();
            await sending;
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await CloseAsync(_closing?.Status ?? WebSocketCloseStatus.NormalClosure, _closing?.Reason);
        }
    }

    /// <summary>
    /// Runs a write's whole call after the writes before it with the same order key, or with none, on the socket's
    /// queue. A socket that closes still runs every write it queued.
    /// </summary>
    public Task<T> QueueWriteAsync<T>(string? order, Func<Task<T>> run)
    {
        if (order is not null)
        {
            lock (_ordered)
            {
                var write = AfterAsync(_ordered.GetValueOrDefault(order), run);
                _ordered[order] = write;
                write.ContinueWith(_ =>
                {
                    lock (_ordered)
                        if (_ordered.GetValueOrDefault(order) == write)
                            _ordered.Remove(order);
                }, TaskScheduler.Default);
                return write;
            }
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writes.Writer.TryWrite(async () =>
            {
                try
                {
                    done.SetResult(await run());
                }
                catch (Exception error)
                {
                    done.SetException(error);
                }
            }))
            done.SetException(new OperationCanceledException("The socket closed."));
        return done.Task;
    }

    private static async Task<T> AfterAsync<T>(Task? before, Func<Task<T>> run)
    {
        if (before is not null)
            await before.ContinueWith(_ => { }, TaskScheduler.Default);
        return await run();
    }

    // The first frame must be a hello with this protocol and the token; nothing is sent before it.
    private async Task<bool> HelloAsync(byte[] token, string machine, CancellationToken stopping)
    {
        string? text;
        try
        {
            text = await ReceiveAsync(stopping);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        {
            return false;
        }

        if (_closing is { } closing)
            await CloseAsync(closing.Status, closing.Reason);
        else if (Read(text) is not { T: "hello", Protocol: Protocol } hello)
            await CloseAsync((WebSocketCloseStatus)4400, "expected a hello");
        else if (hello.Token is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Token), token))
            await CloseAsync((WebSocketCloseStatus)4401, "unauthorized");
        else
        {
            await socket.SendAsync(SocketDispatcher.Frame(new WelcomeFrame(Protocol, machine), SocketJson.Default.WelcomeFrame),
                WebSocketMessageType.Text, endOfMessage: true, stopping);
            return true;
        }

        return false;
    }

    // False when the frame isn't one this protocol has: the socket closes with 4400.
    private async Task<bool> HandleAsync(string text)
    {
        switch (Read(text))
        {
            case { T: "call", Id: { } id, Feature: not null, Method: not null } call:
                var cancel = new CancellationTokenSource();
                if (_running.TryAdd(id, cancel))
                    _ = AnswerAsync(call, id, cancel);
                else
                    Send(SocketDispatcher.Frame(new ErrorFrame(id, "bad_args", $"call {id} is already running"), SocketJson.Default.ErrorFrame));
                return true;
            case { T: "sub", Id: { } id, Topic: not null } sub:
                if (_subscriptions.ContainsKey(id))
                    Send(SocketDispatcher.Frame(new ErrorFrame(id, "bad_args", $"subscription {id} is already open"), SocketJson.Default.ErrorFrame));
                else if (calls.Subscribe(sub, id, this) is { } subscription)
                    _subscriptions[id] = subscription;
                return true;
            case { T: "unsub", Id: { } id }:
                if (_subscriptions.Remove(id, out var unsubscribed))
                    calls.Unsubscribe(unsubscribed);
                return true;
            case { T: "cancel", Id: { } id }:
                // Never disposed, so a cancel racing the call's end is safe.
                if (_running.TryGetValue(id, out var running))
                    await running.CancelAsync();
                return true;
            default:
                _closing = ((WebSocketCloseStatus)4400, "not a frame this protocol has");
                return false;
        }
    }

    private async Task AnswerAsync(InFrame call, long id, CancellationTokenSource cancel)
    {
        try
        {
            Send(await calls.AnswerAsync(call, id, this, cancel.Token));
        }
        finally
        {
            _running.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Queues a frame for the sender and returns at once, so a publish under the writer's lock never waits on a socket.
    /// Answers, states and changes share this one queue, so a write's change goes out before its result. A frame
    /// <paramref name="from"/> a subscription that has closed by its turn isn't sent: a closed tab really stops.
    /// </summary>
    internal void Send(ReadOnlyMemory<byte> frame, TopicSet.Subscription? from = null)
    {
        // A page that stops reading is closed, and so is one sent a change over 4 MB; it reconnects and loads again.
        if (frame.Length > MaxFrame || Interlocked.Add(ref _waiting, frame.Length) > MaxWaiting)
            socket.Abort();
        else
            _outbox.Writer.TryWrite((frame, from));
    }

    private async Task SendLoopAsync()
    {
        await foreach (var (frame, from) in _outbox.Reader.ReadAllAsync())
        {
            try
            {
                if (socket.State == WebSocketState.Open && from is not { Open: false })
                    await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (Exception error) when (error is WebSocketException or ObjectDisposedException)
            {
            }
            Interlocked.Add(ref _waiting, -frame.Length);
        }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var write in _writes.Reader.ReadAllAsync())
            await write();
    }

    // One whole frame's text, or null once the page closes or a frame runs over 4 MB.
    private async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var frame = new ArrayBufferWriter<byte>();
        while (true)
        {
            var received = await socket.ReceiveAsync(frame.GetMemory(16 * 1024), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                return null;
            frame.Advance(received.Count);
            if (frame.WrittenCount > MaxFrame)
            {
                _closing = (WebSocketCloseStatus.MessageTooBig, "a frame over 4 MB");
                return null;
            }
            if (received.EndOfMessage)
                return Encoding.UTF8.GetString(frame.WrittenSpan);
        }
    }

    private static InFrame? Read(string? text)
    {
        if (text is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize(text, SocketJson.Default.InFrame);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string? reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2), time);
            await socket.CloseOutputAsync(status, reason, timeout.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        {
        }
    }
}
