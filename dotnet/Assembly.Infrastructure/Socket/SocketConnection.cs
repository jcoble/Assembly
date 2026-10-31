using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Socket;

/// <summary>
/// One page's socket (plan 5.1): its <c>hello</c> first, then one receive loop, one sender, and one write queue that
/// runs its writes one at a time in arrival order. A frame over 4 MB, or more than 16 MB waiting to be sent, closes
/// it. Closing cancels its reads; writes already queued still commit.
/// </summary>
internal sealed class SocketConnection(WebSocket socket, SocketDispatcher calls, TimeProvider time, bool bulk = false, bool terminal = false)
{
    internal const int Protocol = 1;
    internal const int MaxFrame = 4 * 1024 * 1024;
    private const long MaxWaiting = 16 * 1024 * 1024;
    private static long _last;

    private readonly CancellationTokenSource _closed = new();
    private readonly Channel<(ReadOnlyMemory<byte> Frame, ISocketSubscription? From)> _outbox =
        Channel.CreateUnbounded<(ReadOnlyMemory<byte>, ISocketSubscription?)>(new() { SingleReader = true });
    private readonly Channel<Func<Task>> _writes = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    // The last write queued under each order key, until it ends. Only the receive loop adds one.
    private readonly Dictionary<string, Task> _ordered = [];
    // Each running call's cancel, by its id.
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    // Running answers and subscription work, waited for as the socket ends.
    private readonly BackgroundWork _answers = new(time, Console.Error);
    // This socket's subscriptions, by their sub id. Only the receive loop, and then its finally, touch it.
    private readonly Dictionary<long, ISocketSubscription> _subscriptions = [];
    private long _waiting;
    // Why the socket closes, once the receive loop ends; closed after the last answer is sent, since a socket can't
    // send two things at once.
    private (WebSocketCloseStatus Status, string Reason)? _closing;

    public string Id { get; } = $"socket-{Interlocked.Increment(ref _last)}";

    /// <summary>True on <c>/socket/bulk</c>, which serves only the bulk calls.</summary>
    public bool Bulk => bulk;

    /// <summary>True on <c>/socket/terminal</c>, which serves only terminal output.</summary>
    public bool Terminal => terminal;

    /// <summary>Fires as the socket closes; a read's token is linked to it, a write's isn't.</summary>
    public CancellationToken Closed => _closed.Token;

    public async Task RunAsync(byte[] token, string machine, CancellationToken stopping)
    {
        if (!await HelloAsync(token, machine, stopping))
            return;
        // A drop or the host stopping cancels the reads at once, even one the receive loop is busy with (a sub's state).
        using var stopReads = stopping.Register(_closed.Cancel);

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
            // Every write has run; the reads still answering were cancelled as the socket closed.
            await _answers.StopAsync(TimeSpan.FromSeconds(5));
            calls.ConnectionClosed(Id);
            _outbox.Writer.TryComplete();
            await sending;
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await CloseAsync(_closing?.Status ?? WebSocketCloseStatus.NormalClosure, _closing?.Reason);
        }
    }

    /// <summary>
    /// Runs a write's whole call after the writes before it with the same order key, or with none, on the socket's
    /// queue. A socket that closes still runs every write it queued. A write cancelled while it waits answers at once;
    /// it still runs in its turn, where it ends at once and drops what it held, as any failed write does.
    /// </summary>
    public Task<T> QueueWriteAsync<T>(string? order, Func<Task<T>> run, CancellationToken cancellation)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = cancellation.Register(() => done.TrySetCanceled(cancellation));
        async Task Turn()
        {
            // Waits for a cancel already firing; from here on a cancel is the handler's to see.
            waiting.Dispose();
            try
            {
                done.TrySetResult(await run());
            }
            catch (Exception error)
            {
                done.TrySetException(error);
            }
        }

        if (order is not null)
        {
            lock (_ordered)
            {
                var write = AfterAsync(_ordered.GetValueOrDefault(order), Turn);
                _ordered[order] = write;
                write.ContinueWith(_ =>
                {
                    lock (_ordered)
                        if (_ordered.GetValueOrDefault(order) == write)
                            _ordered.Remove(order);
                }, TaskScheduler.Default);
            }
        }
        else if (!_writes.Writer.TryWrite(Turn))
            done.TrySetException(new OperationCanceledException("The socket closed."));
        return done.Task;
    }

    // The write starts on the thread pool, never inline: off the order lock and the receive loop, so a handler's work
    // before its first await holds up neither, as the socket's queue and reads already do.
    private static async Task AfterAsync(Task? before, Func<Task> run)
    {
        await Task.Yield();
        if (before is not null)
            await before.ContinueWith(_ => { }, TaskScheduler.Default);
        await run();
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
                    _answers.Start($"call {id}", stopping => AnswerAsync(call, id, cancel, stopping));
                else
                    Send(SocketDispatcher.Frame(new ErrorFrame(id, "bad_args", $"call {id} is already running"), SocketJson.Default.ErrorFrame));
                return true;
            case { T: "sub", Id: { } id, Topic: not null } sub:
                if (_subscriptions.ContainsKey(id))
                    Send(SocketDispatcher.Frame(new ErrorFrame(id, "bad_args", $"subscription {id} is already open"), SocketJson.Default.ErrorFrame));
                else if (calls.Subscribe(sub, id, this) is { } subscription)
                {
                    _subscriptions[id] = subscription;
                    _answers.Start($"subscription {id}", _ => subscription.Completion);
                }
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
            case { T: "ack", Id: { } id } ack:
                try
                {
                    if (ack.Stream != "engine" || ack.After is not { } after ||
                        !_subscriptions.TryGetValue(id, out var acknowledged) || acknowledged is not IEngineSubscription engine)
                        throw new ArgumentException("Unknown engine subscription");
                    engine.Acknowledge(after);
                }
                catch (ArgumentException error)
                {
                    Send(SocketDispatcher.Frame(new ErrorFrame(id, "bad_args", error.Message), SocketJson.Default.ErrorFrame));
                }
                return true;
            default:
                _closing = ((WebSocketCloseStatus)4400, "not a frame this protocol has");
                return false;
        }
    }

    private async Task AnswerAsync(InFrame call, long id, CancellationTokenSource cancel, CancellationToken stopping)
    {
        using var stopped = stopping.Register(cancel.Cancel);
        try
        {
            Send(await calls.AnswerAsync(call, id, this, cancel.Token));
        }
        finally
        {
            _running.TryRemove(id, out _);
        }
    }

    internal void Abort() => socket.Abort();

    /// <summary>
    /// Queues a frame for the sender and returns at once, so a publish under the writer's lock never waits on a socket.
    /// Answers, states and changes share this one queue, so a write's change goes out before its result. A frame
    /// <paramref name="from"/> a subscription that has closed by its turn isn't sent: a closed tab really stops.
    /// </summary>
    internal void Send(ReadOnlyMemory<byte> frame, ISocketSubscription? from = null)
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
                if (socket.State == WebSocketState.Open && from?.Closed.IsCancellationRequested != true)
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
