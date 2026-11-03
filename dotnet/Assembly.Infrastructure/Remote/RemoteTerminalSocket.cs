using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Terminal;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Socket;

namespace Assembly.Infrastructure.Remote;

/// <summary>One terminal-only connection through this machine's tunnel. Owns reconnects, reader and pending calls.</summary>
internal sealed class RemoteTerminalSocket(string name, Uri address, string token, ITopics topics,
    Func<string?, CancellationToken, Task<RemoteMachine.OwnerConnectionBinding>> captureOwner) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _send = new(1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private sealed record Follow(long Subscription, Func<bool> Desired);
    private readonly ConcurrentDictionary<string, Follow> _following = new();
    private ClientWebSocket? _connected;
    private RemoteMachine.OwnerConnectionBinding? _connectedOwner;
    private string? _engine;
    private readonly ConcurrentDictionary<string, string> _known = new();
    private readonly ConcurrentDictionary<string, byte> _missing = new();
    private readonly ConcurrentDictionary<string, byte> _ended = new();
    private TaskCompletionSource<ClientWebSocket> _ready = NewReady();
    private Task? _runner;
    private long _next;

    private static TaskCompletionSource<ClientWebSocket> NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Qualify(string id) => $"assembly-remote-terminal://{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(id)}";

    internal Task<string> RegisterAsync(string originalEngine, string hostTerminalId, CancellationToken ct) =>
        RegisterCoreAsync(originalEngine, hostTerminalId, null, ct);

    internal Task<string> RegisterAsync(string originalEngine, string hostTerminalId,
        RemoteMachine.OwnerConnectionBinding originalOwner, CancellationToken ct)
    {
        if (originalOwner is null) throw SourceChanged();
        return RegisterCoreAsync(originalEngine, hostTerminalId, originalOwner, ct);
    }

    private async Task<string> RegisterCoreAsync(string originalEngine, string hostTerminalId,
        RemoteMachine.OwnerConnectionBinding? originalOwner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(originalEngine) || string.IsNullOrWhiteSpace(hostTerminalId) ||
            originalOwner is not null && (originalOwner.Machine != originalEngine || originalOwner.IsInvalidated)) throw SourceChanged();
        await CaptureBeforeAsync(originalEngine, ct, originalOwner);
        lock (_gate)
        {
            if (originalOwner?.IsInvalidated == true) throw SourceChanged();
            _known[hostTerminalId] = originalEngine;
        }
        return Qualify(hostTerminalId);
    }
    private string RequireKnown(string id)
    {
        if (!_known.TryGetValue(id, out var engine)) throw SourceChanged();
        if (_missing.ContainsKey(id)) throw new CallFailed("The original terminal is no longer present on its owner.", "terminal_missing");
        return engine;
    }
    private async Task CaptureBeforeAsync(string? expected, CancellationToken ct,
        RemoteMachine.OwnerConnectionBinding? originalOwner = null)
    {
        if (originalOwner is not null && (originalOwner.IsInvalidated || originalOwner.Machine != expected)) throw SourceChanged();
        string? original;
        lock (_gate) original = _engine;
        if (original is not null && expected is not null && original != expected) throw SourceChanged();
        var owner = await captureOwner(original ?? expected, ct); // Never while holding terminal send/Topics locks.
        if (originalOwner is not null && (!ReferenceEquals(originalOwner, owner) || originalOwner.IsInvalidated)) throw SourceChanged();
        lock (_gate)
        {
            if (_engine is not null && _engine != owner.Machine || expected is not null && expected != owner.Machine) throw SourceChanged();
            _engine ??= owner.Machine;
        }
        if (owner.IsInvalidated) throw SourceChanged();
    }
    private void RequireConnectedOwner(ClientWebSocket socket)
    {
        lock (_gate)
            if (!ReferenceEquals(socket, _connected) || _connectedOwner is not { } owner || owner.IsInvalidated || owner.Machine != _engine)
                throw SourceChanged();
    }
    private static CallFailed SourceChanged() => new("The terminal's original owning source is unavailable or changed.", "source_changed");

    public async Task<TResult> CallAsync<TArgs, TResult>(string method, TArgs args, JsonTypeInfo<TArgs> input,
        JsonTypeInfo<TResult> output, CancellationToken cancellation)
    {
        var arguments = JsonSerializer.SerializeToElement(args, input);
        var original = method is "start" or "list" ? null : RequireKnown(arguments.GetProperty("terminal").GetString() ?? throw SourceChanged());
        await CaptureBeforeAsync(original, cancellation);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        var socket = await ConnectedAsync(linked.Token);
        var id = Interlocked.Increment(ref _next);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await SendAsync(socket, new RemoteTerminalFrame("call", id, Feature: "terminal", Method: method, Args: arguments), linked.Token);
            var result = (await answer.Task.WaitAsync(linked.Token)).Deserialize(output)!;
            await _send.WaitAsync(linked.Token);
            try
            {
                RequireConnectedOwner(socket);
                if (method == "start" && result is TerminalInfo made) _known[made.Id] = _engine!;
                if (method == "list" && result is IReadOnlyList<TerminalInfo> listed)
                    foreach (var item in listed)
                    {
                        _known[item.Id] = _engine!;
                        if (item.ExitCode is not null) _ended[item.Id] = 0;
                    }
            }
            finally { _send.Release(); }
            return result;
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

    public async Task FollowAsync(string id, CancellationToken cancellation, Func<bool>? desired = null)
    {
        var original = RequireKnown(id);
        await CaptureBeforeAsync(original, cancellation);
        // A confirmed ended handle still supports replay, but has no future output to follow.
        if (_ended.ContainsKey(id)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        var socket = await ConnectedAsync(linked.Token);
        await _send.WaitAsync(linked.Token);
        try
        {
            RequireConnectedOwner(socket);
            if (_ended.ContainsKey(id) || desired is not null && !desired()) return;
            var follow = new Follow(Interlocked.Increment(ref _next), desired ?? (() => true));
            if (_following.TryAdd(id, follow))
                await SendLocked(socket, new RemoteTerminalFrame("sub", follow.Subscription, Topic: "terminal", Key: id), linked.Token);
        }
        finally { _send.Release(); }
    }

    public async Task UnfollowAsync(string id, CancellationToken cancellation, Func<bool>? desired = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        await _send.WaitAsync(linked.Token);
        try
        {
            if (desired is not null && desired()) return;
            if (!_following.TryRemove(id, out var follow)) return;
            ClientWebSocket? socket;
            lock (_gate) socket = _connectedOwner is { IsInvalidated: false } ? _connected : null;
            // Removal while disconnected is local. Never establish a connection merely to unsubscribe.
            if (socket?.State == WebSocketState.Open)
                await SendLocked(socket, new RemoteTerminalFrame("unsub", follow.Subscription), linked.Token);
        }
        finally { _send.Release(); }
    }

    private Task<ClientWebSocket> ConnectedAsync(CancellationToken cancellation)
    {
        lock (_gate)
        {
            _stop.Token.ThrowIfCancellationRequested();
            _runner ??= Task.Run(RunAsync, CancellationToken.None);
            if (!_ready.Task.IsCompletedSuccessfully && _wake.CurrentCount == 0) _wake.Release();
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
                RemoteMachine.OwnerConnectionBinding? owner = null;
                CancellationTokenRegistration invalidated = default;
                CancellationTokenSource? connectionStop = null;
                try
                {
                    string? original; lock (_gate) original = _engine;
                    owner = await captureOwner(original, _stop.Token);
                    if (original is null || owner.Machine != original || owner.IsInvalidated) throw SourceChanged();
                    invalidated = owner.Invalidated.Register(socket.Abort);
                    connectionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, owner.Invalidated);
                    var connectionCt = connectionStop.Token;
                    var uri = new UriBuilder(address) { Scheme = address.Scheme == "https" ? "wss" : "ws", Path = "/socket/terminal", Query = "" }.Uri;
                    await socket.ConnectAsync(uri, connectionCt);
                    await SendAsync(socket, new RemoteTerminalFrame("hello", Token: token, Protocol: 1), connectionCt);
                    var welcome = await ReceiveAsync(socket, connectionCt);
                    var protocol = welcome.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var parsedProtocol) ? parsedProtocol : 0;
                    if (!welcome.TryGetProperty("t", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "welcome" || protocol != SocketConnection.Protocol)
                        throw new CallFailed($"{name} speaks protocol {protocol}; this computer speaks protocol {SocketConnection.Protocol}");
                    var format = welcome.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var parsedFormat) ? parsedFormat : 0;
                    if (format != RowStore.FormatVersion)
                        throw new CallFailed($"{name} stores rows in format {format}; this computer reads format {RowStore.FormatVersion}");
                    if (!welcome.TryGetProperty("engine", out var actualEngine) || actualEngine.GetString() != owner.Machine) throw SourceChanged();
                    var recovering = !_following.IsEmpty;
                    await _send.WaitAsync(_stop.Token);
                    try
                    {
                        lock (_gate) { _connected = socket; _connectedOwner = owner; }
                        if (owner.IsInvalidated) throw SourceChanged();
                        reader = ReadAsync(socket, connectionCt);
                        foreach (var (key, follow) in _following)
                        {
                            if (!follow.Desired()) { _following.TryRemove(key, out _); continue; }
                            await SendLocked(socket, new RemoteTerminalFrame("sub", follow.Subscription, Topic: "terminal", Key: key), _stop.Token);
                        }
                    }
                    finally { _send.Release(); }
                    lock (_gate) _ready.TrySetResult(socket);
                    // Subscription first, then catch-up. Live rows may overlap; the window deduplicates by offset.
                    if (recovering) await CatchUpAsync(socket);
                    backoff = TimeSpan.FromSeconds(1);
                    await reader;
                }
                catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or JsonException or CallFailed or MachineUnreachable or ObjectDisposedException || owner?.IsInvalidated == true)
                {
                    var failure = new CallFailed($"{name}: terminal connection lost ({error.Message})");
                    socket.Abort();
                    lock (_gate)
                    {
                        if (ReferenceEquals(_connected, socket)) _connected = null;
                        _ready.TrySetException(failure);
                        _ready = NewReady();
                    }
                    foreach (var answer in _pending.Values) answer.TrySetException(failure);
                    if (!_stop.IsCancellationRequested)
                        foreach (var pair in _following.ToArray())
                            await PublishAvailability(pair.Key, pair.Value, TerminalUnavailable.ConnectionLost);
                    if (error is CallFailed { Reason: "source_changed" }) break;
                }
                finally
                {
                    socket.Abort();
                    invalidated.Dispose();
                    if (reader is not null)
                        try { await reader; }
                        catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or JsonException or CallFailed or MachineUnreachable or ObjectDisposedException) { }
                    connectionStop?.Dispose();
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

    private async Task CatchUpAsync(ClientWebSocket socket)
    {
        var json = TerminalJson.Default;
        var followed = _following.ToArray();
        var listed = await CallAsync("list", new ListArgs(), json.ListArgs, json.IReadOnlyListTerminalInfo, _stop.Token);
        var present = listed.ToDictionary(info => info.Id);
        foreach (var (id, follow) in followed)
        {
            if (!IsCurrent(socket, id, follow)) continue;
            if (!present.TryGetValue(id, out var info))
            {
                _missing[id] = 0;
                await PublishAvailability(id, follow, TerminalUnavailable.TerminalMissing);
                await RemoveCurrent(socket, id, follow);
                continue;
            }
            var boundary = await CallAsync("read", new ReadArgs(id, long.MaxValue), json.ReadArgs, json.OutputPiece, _stop.Token);
            await PublishCurrent(socket, id, follow, new OutputRow(boundary.End, boundary.End, []));
            // Read clamps to the actual oldest retained byte; the native live window is not a recovery cap.
            long offset = 0;
            while (offset < boundary.End)
            {
                if (!IsCurrent(socket, id, follow)) break;
                var piece = await CallAsync("read", new ReadArgs(id, offset), json.ReadArgs, json.OutputPiece, _stop.Token);
                if (piece.Offset > boundary.End) { await PublishCurrent(socket, id, follow, new OutputRow(piece.Offset, piece.Offset, [])); break; }
                var length = (int)Math.Min(piece.Data.Length, boundary.End - piece.Offset);
                if (length <= 0) throw new CallFailed("Remote terminal replay made no progress.");
                await PublishCurrent(socket, id, follow, new OutputRow(piece.Offset + length, piece.Offset, piece.Data[..length]));
                offset = piece.Offset + length;
            }
            if (info.ExitCode is { } exit)
            {
                await PublishCurrent(socket, id, follow, new OutputRow(boundary.End + 1, boundary.End, Exit: exit));
                await RemoveCurrent(socket, id, follow);
            }
        }
    }

    private bool IsCurrent(ClientWebSocket socket, string id, Follow follow)
    {
        lock (_gate)
            return ReferenceEquals(socket, _connected) && _connectedOwner is { IsInvalidated: false } owner &&
                owner.Machine == _engine && _known.TryGetValue(id, out var known) && known == owner.Machine && _following.TryGetValue(id, out var current) && ReferenceEquals(current, follow) && follow.Desired();
    }
    private async Task PublishCurrent(ClientWebSocket socket, string id, Follow follow, OutputRow row)
    {
        await _send.WaitAsync(_stop.Token);
        try
        {
            if (IsCurrent(socket, id, follow))
            {
                if (row.Exit is not null) _ended[id] = 0;
                topics.Publish("terminal", Qualify(id), JsonSerializer.Serialize(row, TerminalJson.Default.OutputRow));
            }
        }
        finally { _send.Release(); }
    }
    private async Task PublishAvailability(string id, Follow follow, TerminalUnavailable unavailable)
    {
        await _send.WaitAsync(_stop.Token);
        try
        {
            if (_following.TryGetValue(id, out var current) && ReferenceEquals(current, follow) && follow.Desired())
                topics.Publish("terminal", Qualify(id), JsonSerializer.Serialize(new OutputRow(null, null, Unavailable: unavailable), TerminalJson.Default.OutputRow));
        }
        finally { _send.Release(); }
    }
    private async Task RemoveCurrent(ClientWebSocket socket, string id, Follow follow)
    {
        await _send.WaitAsync(_stop.Token);
        try
        {
            if (_following.TryGetValue(id, out var current) && ReferenceEquals(current, follow))
            {
                _following.TryRemove(id, out _);
                if (socket.State == WebSocketState.Open) await SendLocked(socket, new RemoteTerminalFrame("unsub", follow.Subscription), _stop.Token);
            }
        }
        finally { _send.Release(); }
    }

    private async Task ReadAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReceiveAsync(socket, ct);
                RequireConnectedOwner(socket);
                switch (frame.GetProperty("t").GetString())
                {
                    case "result":
                        if (_pending.TryGetValue(frame.GetProperty("id").GetInt64(), out var answer))
                            answer.TrySetResult(frame.GetProperty("result").Clone());
                        break;
                    case "error":
                        if (_pending.TryGetValue(frame.GetProperty("id").GetInt64(), out var failed))
                            failed.TrySetException(new CallFailed($"{name}: {frame.GetProperty("message").GetString()}", frame.TryGetProperty("code", out var code) ? code.GetString() : null));
                        break;
                    case "change":
                        var key = frame.GetProperty("key").GetString()!;
                        if (!_following.TryGetValue(key, out var follow) || !frame.TryGetProperty("sub", out var subscription) || subscription.GetInt64() != follow.Subscription) break;
                        var item = frame.GetProperty("item").Deserialize(TerminalJson.Default.OutputRow) ?? throw new CallFailed("Invalid terminal output.");
                        await PublishCurrent(socket, key, follow, item);
                        if (item.Exit is not null) await RemoveCurrent(socket, key, follow);
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
        try { await SendLocked(socket, frame, cancellation); }
        finally { _send.Release(); }
    }

    private Task SendLocked(ClientWebSocket socket, RemoteTerminalFrame frame, CancellationToken ct)
    {
        if (frame.T != "hello") RequireConnectedOwner(socket);
        return socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame, RemoteTerminalJson.Default.RemoteTerminalFrame), WebSocketMessageType.Text, true, ct);
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
        lock (_gate) runner = _runner;
        await _stop.CancelAsync();
        if (runner is not null) await runner;
    }
}
