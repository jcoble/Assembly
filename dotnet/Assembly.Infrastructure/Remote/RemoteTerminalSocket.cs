using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Terminal;

namespace Assembly.Infrastructure.Remote;

/// <summary>One terminal-only connection through this machine's tunnel. Owns reconnects, reader and pending calls.</summary>
internal sealed class RemoteTerminalSocket(string name, Uri address, string token, ITopics topics) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _send = new(1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, long> _following = new();
    private TaskCompletionSource<ClientWebSocket> _ready = NewReady();
    private Task? _runner;
    private long _next;

    private static TaskCompletionSource<ClientWebSocket> NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Qualify(string id) => $"assembly-remote-terminal://{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(id)}";

    public async Task<TResult> CallAsync<TArgs, TResult>(string method, TArgs args, JsonTypeInfo<TArgs> input,
        JsonTypeInfo<TResult> output, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        var socket = await ConnectedAsync(linked.Token);
        var id = Interlocked.Increment(ref _next);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await SendAsync(socket, new RemoteTerminalFrame("call", id, Feature: "terminal", Method: method, Args: JsonSerializer.SerializeToElement(args, input)), linked.Token);
            return (await answer.Task.WaitAsync(linked.Token)).Deserialize(output)!;
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        {
            await SendAsync(socket, new RemoteTerminalFrame("cancel", id), _stop.Token);
            // A cancelled write finishes only after its remote writer has stopped.
            try { await answer.Task.WaitAsync(_stop.Token); }
            catch (CallFailed) { }
            throw;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public async Task FollowAsync(string id, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        var socket = await ConnectedAsync(linked.Token);
        if (_following.TryAdd(id, Interlocked.Increment(ref _next)))
            await SendAsync(socket, new RemoteTerminalFrame("sub", _following[id], Topic: "terminal", Key: id), linked.Token);
    }

    public async Task UnfollowAsync(string id, CancellationToken cancellation)
    {
        if (!_following.TryRemove(id, out var sub)) return;
        var socket = await ConnectedAsync(cancellation);
        await SendAsync(socket, new RemoteTerminalFrame("unsub", sub), cancellation);
    }

    private Task<ClientWebSocket> ConnectedAsync(CancellationToken cancellation)
    {
        lock (_gate)
        {
            _stop.Token.ThrowIfCancellationRequested();
            _runner ??= Task.Run(RunAsync, CancellationToken.None);
            if (_wake.CurrentCount == 0) _wake.Release();
            return _ready.Task.WaitAsync(cancellation);
        }
    }

    private async Task RunAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (_following.IsEmpty) await _wake.WaitAsync(_stop.Token);
                using var socket = new ClientWebSocket();
                Task? reader = null;
                try
                {
                    var uri = new UriBuilder(address) { Scheme = address.Scheme == "https" ? "wss" : "ws", Path = "/socket/terminal", Query = "" }.Uri;
                    await socket.ConnectAsync(uri, _stop.Token);
                    await SendAsync(socket, new RemoteTerminalFrame("hello", Token: token, Protocol: 1), _stop.Token);
                    var welcome = await ReceiveAsync(socket, _stop.Token);
                    if (welcome.GetProperty("t").GetString() != "welcome") throw new CallFailed($"{name} refused the terminal connection");
                    reader = ReadAsync(socket);
                    var recovering = !_following.IsEmpty;
                    foreach (var (key, sub) in _following)
                        await SendAsync(socket, new RemoteTerminalFrame("sub", sub, Topic: "terminal", Key: key), _stop.Token);
                    lock (_gate) _ready.TrySetResult(socket);
                    // Subscription first, then catch-up. Live rows may overlap; the window deduplicates by offset.
                    if (recovering) await CatchUpAsync();
                    backoff = TimeSpan.FromSeconds(1);
                    await reader;
                }
                catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or JsonException or CallFailed)
                {
                    var failure = new CallFailed($"{name}: terminal connection lost ({error.Message})");
                    socket.Abort();
                    lock (_gate)
                    {
                        _ready.TrySetException(failure);
                        _ready = NewReady();
                    }
                    foreach (var answer in _pending.Values) answer.TrySetException(failure);
                }
                finally
                {
                    socket.Abort();
                    if (reader is not null)
                        try { await reader; }
                        catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or JsonException or CallFailed) { }
                }
                if (!_following.IsEmpty)
                {
                    await Task.Delay(backoff, _stop.Token);
                    backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            lock (_gate) _ready.TrySetCanceled(_stop.Token);
            foreach (var answer in _pending.Values) answer.TrySetCanceled(_stop.Token);
        }
    }

    private async Task CatchUpAsync()
    {
        var json = TerminalJson.Default;
        var followed = _following.Keys.ToArray();
        var listed = await CallAsync("list", new ListArgs(), json.ListArgs, json.IReadOnlyListTerminalInfo, _stop.Token);
        var present = listed.Select(info => info.Id).ToHashSet();
        foreach (var missing in followed.Where(id => !present.Contains(id)))
            await UnfollowAsync(missing, _stop.Token);
        foreach (var info in listed.Where(info => _following.ContainsKey(info.Id)))
        {
            var piece = await CallAsync("read", new ReadArgs(info.Id, long.MaxValue), json.ReadArgs, json.OutputPiece, _stop.Token);
            // An empty boundary row exposes missed bytes even when the remote shell is now quiet.
            var row = info.ExitCode is { } exit ? new OutputRow(piece.End + 1, piece.End, Exit: exit)
                : new OutputRow(piece.End, piece.End, []);
            topics.Publish("terminal", Qualify(info.Id), JsonSerializer.Serialize(row, json.OutputRow));
            if (info.ExitCode is not null) await UnfollowAsync(info.Id, _stop.Token);
        }
    }

    private async Task ReadAsync(ClientWebSocket socket)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frame = await ReceiveAsync(socket, _stop.Token);
                switch (frame.GetProperty("t").GetString())
                {
                    case "result":
                        if (_pending.TryGetValue(frame.GetProperty("id").GetInt64(), out var answer))
                            answer.TrySetResult(frame.GetProperty("result").Clone());
                        break;
                    case "error":
                        if (_pending.TryGetValue(frame.GetProperty("id").GetInt64(), out var failed))
                            failed.TrySetException(new CallFailed($"{name}: {frame.GetProperty("message").GetString()}"));
                        break;
                    case "change":
                        var key = frame.GetProperty("key").GetString()!;
                        var item = frame.GetProperty("item");
                        topics.Publish("terminal", Qualify(key), item.GetRawText());
                        if (item.TryGetProperty("exit", out _) && _following.TryRemove(key, out var sub))
                            await SendAsync(socket, new RemoteTerminalFrame("unsub", sub), _stop.Token);
                        break;
                }
            }
        }
        finally
        {
            foreach (var answer in _pending.Values)
                answer.TrySetException(new CallFailed($"{name}: terminal connection closed"));
        }
    }

    private async Task SendAsync(ClientWebSocket socket, RemoteTerminalFrame frame, CancellationToken cancellation)
    {
        await _send.WaitAsync(cancellation);
        try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame, RemoteTerminalJson.Default.RemoteTerminalFrame), WebSocketMessageType.Text, true, cancellation); }
        finally { _send.Release(); }
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer, cancellation);
            if (received.MessageType == WebSocketMessageType.Close) throw new CallFailed("terminal socket closed");
            message.Write(buffer, 0, received.Count);
            if (message.Length > 4 * 1024 * 1024) throw new CallFailed("terminal frame is over 4 MB");
        } while (!received.EndOfMessage);
        using var json = JsonDocument.Parse(message.ToArray());
        return json.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Task? runner;
        lock (_gate) { _stop.Cancel(); runner = _runner; }
        if (runner is not null) await runner;
    }
}
