using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Reading;
using System.Threading.Channels;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private async Task<ClientWebSocket> ConnectSocketAsync(CancellationToken cancellationToken)
    {
        await _socketSend.WaitAsync(cancellationToken);
        try
        {
            if (_socket is { State: WebSocketState.Open } existing) return existing;
            var socket = new ClientWebSocket();
            try
            {
                await OpenSocketAsync(socket, "/socket", cancellationToken);
                _sessionsSub = Interlocked.Increment(ref _nextSub);
                await SendFrameAsync(socket, $"{{\"t\":\"sub\",\"id\":{_sessionsSub},\"topic\":\"sessions\",\"sessionTitleParts\":1}}", cancellationToken);
                foreach (var (id, sub) in _subscriptions)
                    if (sub.Owner is null)
                        await SendFrameAsync(socket, SubFrame(id, sub.Session, _hub.LastSeq(sub.Session)), cancellationToken);
                    else sub.Frames.Writer.TryComplete(new CallFailed("The selected row source changed.", "source_changed"));
                foreach (var pending in _calls.Values)
                    pending.Completion.TrySetException(new MachineUnreachable(_name, $"{_name}: machine socket closed", mayHaveBeenSent: pending.Sent));
                _calls.Clear();
                CloseGitSubscriptions(_socket);
                _socketFailure = null;
                InvalidateEngine();
                _socket = socket;
                BeginSessionPublications(socket);
                SocketOpened?.Invoke();
                return socket;
            }
            catch (Exception error) when (error is WebSocketException or IOException)
            {
                socket.Dispose();
                throw _socketFailure = Unreachable("didn't answer", $"{_name} didn't answer: {error.Message}", new HttpRequestException(error.Message, error));
            }
            catch (MachineUnreachable error) { socket.Dispose(); _socketFailure = error; throw; }
            catch { socket.Dispose(); throw; }
        }
        finally { _socketSend.Release(); }
    }

    private Task<JsonElement> CallAsync(string feature, string method, object args, CancellationToken cancellationToken) =>
        CallAsync(feature, method, JsonSerializer.SerializeToElement(args, args.GetType(), SessionsJson.Default), false, cancellationToken);

    internal Task<JsonElement> CallAsync(string feature, string method, JsonElement args, bool bulk, CancellationToken cancellationToken, string? expectedEngineId = null, ClientWebSocket? expectedConnection = null,
        OwnerConnectionBinding? expectedOwner = null, Assembly.Infrastructure.Engine.EngineMirror.LiveMirrorBinding? expectedMirror = null,
        Assembly.Infrastructure.Engine.Relays.FrozenMachineStartIntent? startIntent = null) =>
        CallCoreAsync(feature, method, args, bulk, cancellationToken, expectedEngineId, expectedConnection, expectedOwner, expectedMirror, startIntent, false);

    private readonly HashSet<Task> _gitCallDrains = [];
    internal async Task<JsonElement> CallGitWriteAsync(string method, JsonElement args, string expectedEngineId,
        ClientWebSocket expectedConnection, CancellationToken ct, OwnerConnectionBinding? expectedOwner = null,
        Assembly.Infrastructure.Engine.EngineMirror.LiveMirrorBinding? expectedMirror = null)
    {
        if (Assembly.Infrastructure.Chat.SourceControl.GitCallKinds.KindOf(method) != Assembly.Application.Common.Calls.CallKind.Write) throw new CallFailed("forbidden");
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _socketSend.WaitAsync(ct);
        try
        {
            if (_ownerStopping) throw Unreachable("machine socket closed", $"{_name}: machine owner is stopping");
            _gitCallDrains.Add(drained.Task);
        }
        finally { _socketSend.Release(); }
        try { return await CallCoreAsync("git", method, args, false, ct, expectedEngineId, expectedConnection, expectedOwner, expectedMirror, null, true); }
        finally
        {
            await _socketSend.WaitAsync(CancellationToken.None);
            try { _gitCallDrains.Remove(drained.Task); }
            finally { _socketSend.Release(); drained.TrySetResult(); }
        }
    }

    private async Task<JsonElement> CallCoreAsync(string feature, string method, JsonElement args, bool bulk,
        CancellationToken cancellationToken, string? expectedEngineId, ClientWebSocket? expectedConnection,
        OwnerConnectionBinding? expectedOwner, Assembly.Infrastructure.Engine.EngineMirror.LiveMirrorBinding? expectedMirror,
        Assembly.Infrastructure.Engine.Relays.FrozenMachineStartIntent? startIntent, bool gitWrite)
    {
        if (bulk) return await BulkCallAsync(feature, method, args, cancellationToken, expectedEngineId, expectedConnection, expectedOwner, expectedMirror);
        if (_socket is not { State: WebSocketState.Open })
            throw _socketFailure ?? Unreachable("machine socket closed", $"{_name}: machine socket closed");
        var id = Interlocked.Increment(ref _nextSub);
        var pending = new PendingCall();
        ClientWebSocket sentOn;
        await _socketSend.WaitAsync(cancellationToken);
        try
        {
            if (_socket is not { State: WebSocketState.Open } socket)
                throw _socketFailure ?? Unreachable("machine socket closed", $"{_name}: machine socket closed");
            if (expectedEngineId is not null) RequireEngine(expectedEngineId);
            if (expectedConnection is not null && !ReferenceEquals(socket, expectedConnection))
                throw Unreachable("machine socket changed", $"{_name}: owning engine connection changed before send");
            if (expectedOwner is not null)
            {
                RequireOwnerBinding(expectedOwner); RequireLiveMirror(expectedOwner, expectedMirror);
            }
            RequireMatchingBackendForStart(feature, method, args);
            sentOn = socket;
            var json = args.GetRawText();
            startIntent?.SendingNormal(this, socket, feature, method, args, id);
            cancellationToken.ThrowIfCancellationRequested();
            _calls.Add(id, pending);
            pending.Sent = true;
            await SendFrameAsync(socket, $"{{\"t\":\"call\",\"id\":{id},\"feature\":\"{feature}\",\"method\":\"{method}\",\"args\":{json}}}", cancellationToken);
            startIntent?.FrameOrdered();
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
            startIntent?.SendFailed();
            _calls.Remove(id);
            _socket?.Abort();
            if (error is OperationCanceledException && (!gitWrite || !pending.Sent)) throw;
            throw new MachineUnreachable(_name, $"{_name}: machine socket closed", error, pending.Sent);
        }
        catch (Exception) when (startIntent is not null)
        {
            var sending = startIntent.Phase == Assembly.Infrastructure.Engine.Relays.StartIssuePhase.Sending;
            startIntent.SendFailed(); _calls.Remove(id);
            if (sending) _socket?.Abort();
            throw;
        }
        finally { _socketSend.Release(); }
        try
        {
            var frame = await pending.Completion.Task.WaitAsync(cancellationToken);
            if (frame.GetProperty("t").GetString() == "result")
            {
                var result = frame.GetProperty("result");
                if (feature == "engine" && method == "machine")
                {
                    // The mirror's existing identity handshake authorizes only its exact socket.
                    await _socketSend.WaitAsync(cancellationToken);
                    try
                    {
                        if (!ReferenceEquals(_socket, sentOn) || sentOn.State != WebSocketState.Open)
                            throw Unreachable("machine socket changed", $"{_name}: machine socket changed during identity handshake");
                        var identity = result.GetProperty("machine").GetString();
                        if (string.IsNullOrWhiteSpace(identity)) throw new IOException("Invalid remote engine identity");
                        if (_ownerStopping || _socketStop.IsCancellationRequested) throw new OperationCanceledException();
                        if (_engineIdentity is not null && _engineIdentity != identity) InvalidateEngine();
                        _engineIdentity = identity;
                        if (_sessionPublication is null) BeginSessionPublications(sentOn);
                        _sessionPublication!.Identity.TrySetResult(identity);
                    }
                    finally { _socketSend.Release(); }
                }
                if (expectedEngineId is not null)
                {
                    await _socketSend.WaitAsync(cancellationToken);
                    try
                    {
                        if (!ReferenceEquals(_socket, sentOn) || sentOn.State != WebSocketState.Open)
                            throw Unreachable("machine socket changed", $"{_name}: machine socket changed during engine read");
                        RequireEngine(expectedEngineId);
                        if (expectedOwner is not null)
                        {
                            RequireOwnerBinding(expectedOwner); RequireLiveMirror(expectedOwner, expectedMirror);
                        }
                    }
                    finally { _socketSend.Release(); }
                }
                _status.Reachable(_name);
                return result;
            }
            if (gitWrite)
            {
                await _socketSend.WaitAsync(cancellationToken);
                try
                {
                    if (!ReferenceEquals(_socket, sentOn) || !ReferenceEquals(RequireEngine(expectedEngineId!), expectedConnection))
                        throw Unreachable("machine socket changed", $"{_name}: owning engine connection changed");
                }
                finally { _socketSend.Release(); }
            }
            var message = frame.GetProperty("message").GetString()!;
            if (gitWrite && frame.GetProperty("code").GetString() == "uncertain") throw new CallUncertain(_name);
            if (frame.GetProperty("code").GetString() == "unreachable") throw Unreachable(message, message);
            _status.Reachable(_name);
            throw new CallFailed(message, frame.GetProperty("code").GetString());
        }
        catch (OperationCanceledException error) when (gitWrite && pending.Sent && cancellationToken.IsCancellationRequested)
        { throw new MachineUnreachable(_name, $"{_name}: Git operation reply is unconfirmed", error, true); }
        catch (MachineUnreachable error) when (gitWrite && pending.Sent && !error.MayHaveBeenSent)
        { throw new MachineUnreachable(error.Machine, error.Message, error, true); }
        finally
        {
            await _socketSend.WaitAsync(CancellationToken.None);
            try
            {
                _calls.Remove(id);
                if (cancellationToken.IsCancellationRequested && ReferenceEquals(_socket, sentOn) && _socket is { State: WebSocketState.Open } socket)
                    try { await SendFrameAsync(socket, $"{{\"t\":\"cancel\",\"id\":{id}}}", _socketStop.Token); }
                    catch (WebSocketException) { socket.Abort(); }
            }
            finally { _socketSend.Release(); }
        }
    }

    // All followed sessions share this receive loop. A backend restart can leave SSH up, so this machine owns retry.
    private readonly CancellationTokenSource _socketStop = new();
    private readonly SemaphoreSlim _socketSend = new(1), _bulkCall = new(1);
    private readonly Dictionary<long, (string Session, Channel<JsonElement> Frames, OwnerConnectionBinding? Owner)> _subscriptions = [];
    private sealed class PendingCall
    {
        internal readonly TaskCompletionSource<JsonElement> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Sent;
    }
    private readonly Dictionary<long, PendingCall> _calls = [];
    private readonly Dictionary<long, Channel<JsonElement>> _engineSubscriptions = [];
    internal event Action? SocketOpened;
    private ClientWebSocket? _socket, _bulkSocket;
    private string? _engineIdentity, _bulkEngineIdentity;
    private long _sessionsSub;
    private MachineUnreachable? _socketFailure;
    private Task? _socketRun;
    private long _nextSub;
    private readonly Uri _address = null!;
    private readonly string _token = null!;
    private readonly TimeProvider _time = null!;
    private readonly TextWriter _log = null!;

    private IAsyncEnumerable<JsonElement> RowsAsync(string session, long after, CancellationToken cancellationToken) =>
        RowsCoreAsync(session, after, null, null, cancellationToken);

    internal IAsyncEnumerable<JsonElement> BoundRowsAsync(string session, long? after, OwnerConnectionBinding owner,
        EngineMirror.LiveMirrorBinding live, CancellationToken ct) => RowsCoreAsync(session, after, owner, live, ct);

    private async IAsyncEnumerable<JsonElement> RowsCoreAsync(string session, long? after, OwnerConnectionBinding? owner,
        EngineMirror.LiveMirrorBinding? live,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stopping = owner is null ? null : CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, owner.Invalidated, live!.Invalidated);
        var ct = stopping?.Token ?? cancellationToken;
        var frames = System.Threading.Channels.Channel.CreateBounded<JsonElement>(512);
        var id = Interlocked.Increment(ref _nextSub);
        ClientWebSocket? sentOn = null;
        await _socketSend.WaitAsync(ct);
        try
        {
            if (owner is not null) { RequireOwnerBinding(owner); RequireLiveMirror(owner, live); }
            _subscriptions.Add(id, (session, frames, owner));
            _socketRun ??= Task.Run(SocketLoopAsync, CancellationToken.None);
            if (_socket is { State: WebSocketState.Open } socket)
            {
                sentOn = socket;
                await SendFrameAsync(socket, SubFrame(id, session, after,
                    owner is null ? null : new(owner.Machine, 0, null)), ct);
            }
        }
        catch
        {
            _subscriptions.Remove(id);
            if (owner is null) _socket?.Abort();
            throw;
        }
        finally { _socketSend.Release(); }
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                if (owner is not null && (owner.IsInvalidated || live!.IsInvalidated))
                    throw new CallFailed("The selected row source changed.", "source_changed");
                yield return frame;
            }
        }
        finally
        {
            await _socketSend.WaitAsync(CancellationToken.None);
            try
            {
                _subscriptions.Remove(id);
                if (_socket is { State: WebSocketState.Open } socket && (owner is null || ReferenceEquals(socket, sentOn)))
                    try { await SendFrameAsync(socket, $"{{\"t\":\"unsub\",\"id\":{id}}}", _socketStop.Token); }
                    catch (Exception error) when (error is WebSocketException or OperationCanceledException) { socket.Abort(); }
            }
            finally { _socketSend.Release(); }
        }
    }

    private static string SubFrame(long id, string session, long? after, ExpectedSource? expected = null) =>
        $"{{\"t\":\"sub\",\"id\":{id},\"topic\":\"rows\",\"key\":{JsonSerializer.Serialize(session, RowJsonContext.Rows.String)},\"after\":{(after is null ? "null" : after.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}" +
        (expected is null ? "}" : $",\"expectedSource\":{JsonSerializer.Serialize(expected, RowJsonContext.Rows.ExpectedSource)}}}");

    private async Task SocketLoopAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!_socketStop.IsCancellationRequested)
        {
            ClientWebSocket? socket = null;
            var copied = false;
            try
            {
                socket = await ConnectSocketAsync(_socketStop.Token);
                _status.Reachable(_name);
                _follower.Resume();
                while (true)
                {
                    var frame = await ReadFrameAsync(socket, _socketStop.Token);
                    await _socketSend.WaitAsync(_socketStop.Token);
                    try
                    {
                        if (frame.TryGetProperty("id", out var engineId) && _engineSubscriptions.TryGetValue(engineId.GetInt64(), out var engineFrames)
                            && frame.GetProperty("t").GetString() is "engine_snapshot" or "engine_events" or "error")
                        {
                            if (!engineFrames.Writer.TryWrite(frame)) throw new IOException("remote engine queue is full");
                        }
                        else if (ReceiveProviderRuntime(frame, socket)) { }
                        else if ((frame.TryGetProperty("sub", out var gitId) || frame.TryGetProperty("id", out gitId)) &&
                            _gitSubscriptions.TryGetValue(gitId.GetInt64(), out var git) && ReferenceEquals(git.Socket, socket))
                        {
                            if (!git.Frames.Writer.TryWrite(frame)) throw new IOException("remote Git operation queue is full");
                        }
                        else if (frame.GetProperty("t").GetString() == "error" && frame.TryGetProperty("id", out var rowsId)
                            && _subscriptions.TryGetValue(rowsId.GetInt64(), out var refused) && refused.Owner is not null)
                            refused.Frames.Writer.TryComplete(new CallFailed("The owning row subscription was refused.",
                                frame.TryGetProperty("code", out var reason) ? reason.GetString() : "failed"));
                        else if (frame.GetProperty("t").GetString() == "error" && frame.TryGetProperty("id", out var createdId)
                            && _createdTitles.TryGetValue(createdId.GetInt64(), out var created) && ReferenceEquals(created.Socket, socket))
                            created.State.TrySetException(new CallFailed("The owner session metadata was refused.",
                                frame.TryGetProperty("code", out var code) ? code.GetString() : "failed"));
                        else if (frame.TryGetProperty("id", out var callId) && _calls.Remove(callId.GetInt64(), out var pending))
                            pending.Completion.TrySetResult(frame);
                        else if (frame.TryGetProperty("sub", out var sub))
                        {
                            if (_createdTitles.TryGetValue(sub.GetInt64(), out var createdTitle) && ReferenceEquals(createdTitle.Socket, socket))
                            {
                                try { RequireOwnerBinding(createdTitle.Owner); RequireLiveMirror(createdTitle.Owner, createdTitle.Live); }
                                catch (CallFailed)
                                {
                                    createdTitle.State.TrySetException(new CallFailed("The accepting source changed.", "source_changed"));
                                    continue;
                                }
                                if (frame.GetProperty("t").GetString() == "state")
                                {
                                    var entries = frame.GetProperty("items");
                                    if (entries.GetArrayLength() != 1)
                                        createdTitle.State.TrySetException(new CallFailed("The accepting owner returned no session metadata.", "source_changed"));
                                    else
                                    {
                                        var session = entries[0].GetProperty("item").GetProperty("sessionId").GetString()!;
                                        if (session != createdTitle.SessionId)
                                        {
                                            createdTitle.State.TrySetException(new CallFailed("The owner session metadata identity differs.", "source_changed"));
                                            continue;
                                        }
                                        ReceiveSessionPublication(frame, socket, createdTitle.Owner, createdTitle.Live, createdTitle.Cancellation);
                                        createdTitle.State.TrySetResult(_sessionPublication!.Items.TryGetValue(session, out var item) ? item.Resolution?.Work : null);
                                    }
                                }
                                else if (frame.TryGetProperty("item", out var update) &&
                                    update.GetProperty("sessionId").GetString() == createdTitle.SessionId)
                                    ReceiveSessionPublication(frame, socket, createdTitle.Owner, createdTitle.Live, createdTitle.Cancellation);
                            }
                            else if (sub.GetInt64() == _sessionsSub)
                            {
                                ReceiveSessionPublication(frame, socket);
                            }
                            else if (_subscriptions.TryGetValue(sub.GetInt64(), out var target) && !target.Frames.Writer.TryWrite(frame))
                                throw new IOException("remote rows queue is full");
                        }
                    }
                    finally { _socketSend.Release(); }
                    copied |= frame.GetProperty("t").GetString() == "rows";
                }
            }
            catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or MachineUnreachable or ObjectDisposedException)
            {
                if (_socketStop.IsCancellationRequested) break;
                if (error is not MachineUnreachable) _status.Unreachable(_name, "machine socket closed");
                _log.WriteLine($"remote {_name}: socket lost ({error.GetType().Name})");
            }
            finally
            {
                Task titleDrain = Task.CompletedTask;
                await _socketSend.WaitAsync(CancellationToken.None);
                try
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        titleDrain = EndSessionPublications();
                        InvalidateEngine();
                        _socket = null;
                        foreach (var pending in _calls.Values)
                            pending.Completion.TrySetException(new MachineUnreachable(_name, $"{_name}: machine socket closed", mayHaveBeenSent: pending.Sent));
                        _calls.Clear();
                        CloseGitSubscriptions(socket);
                    }
                    socket?.Dispose();
                }
                finally { _socketSend.Release(); }
                await titleDrain;
            }
            if (copied) backoff = TimeSpan.FromSeconds(1);
            try { await Task.Delay(backoff, _time, _socketStop.Token); }
            catch (OperationCanceledException) when (_socketStop.IsCancellationRequested) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    internal async IAsyncEnumerable<JsonElement> EngineFramesAsync(Func<(long? After, CursorAt? At)> cursor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, OwnerConnectionBinding binding)
    {
        var id = Interlocked.Increment(ref _nextSub);
        // Snapshot pages are pulled. Event pages remain bounded by the socket's existing receive path.
        var frames = Channel.CreateBounded<JsonElement>(16);
        await _socketSend.WaitAsync(ct);
        try
        {
            if (_socket is not { State: WebSocketState.Open } socket)
                throw Unreachable("machine socket closed", $"{_name}: machine socket closed");
            socket = RequireOwnerBinding(binding);
            _engineSubscriptions.Add(id, frames);
            var (after, at) = cursor();
            var position = after is null ? "" : $",\"after\":{after}";
            if (at is not null) position += $",\"at\":{JsonSerializer.Serialize(at, EngineJson.Default.CursorAt)}";
            await SendFrameAsync(socket, $"{{\"t\":\"sub\",\"id\":{id},\"stream\":\"engine\",\"topic\":\"mirror\"{position}}}", ct);
        }
        catch { _engineSubscriptions.Remove(id); throw; }
        finally { _socketSend.Release(); }
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct)) yield return frame;
        }
        finally
        {
            await _socketSend.WaitAsync(CancellationToken.None);
            try
            {
                _engineSubscriptions.Remove(id);
                if (_socket is { State: WebSocketState.Open } socket && binding.Matches(this, socket, _ownerLifecycle))
                    try { await SendFrameAsync(socket, $"{{\"t\":\"unsub\",\"id\":{id}}}", _socketStop.Token); }
                    catch (Exception error) when (error is WebSocketException or OperationCanceledException) { socket.Abort(); }
            }
            finally { _socketSend.Release(); }
        }
    }

    private async Task<IReadOnlyList<StoredRow>> ReadRowsAsync(string session, long after, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize($"{{\"session\":{JsonSerializer.Serialize(session, RowJsonContext.Rows.String)},\"after\":{after},\"limit\":{RowsFeature.PageLimit}}}", EngineJson.Default.JsonElement);
        var result = await CallAsync("rows", "after", args, true, ct);
        return result.GetProperty("rows").EnumerateArray().Select(RowWire.Read).ToArray();
    }

    private async Task<string?> OpenSocketAsync(ClientWebSocket socket, string path, CancellationToken cancellationToken)
    {
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_token}");
        var uri = new UriBuilder(_address) { Scheme = _address.Scheme == "https" ? "wss" : "ws", Path = path };
        await socket.ConnectAsync(uri.Uri, cancellationToken);
        await SendFrameAsync(socket, $"{{\"t\":\"hello\",\"protocol\":1,\"token\":{JsonSerializer.Serialize(_token, RowJsonContext.Rows.String)}}}", cancellationToken);
        var welcome = await ReadFrameAsync(socket, cancellationToken);
        if (welcome.GetProperty("t").GetString() != "welcome" || welcome.GetProperty("protocol").GetInt32() != 1)
            throw new IOException("remote socket handshake failed");
        // Copied rows must have this computer's format, before any reachable status or subscription.
        var format = welcome.TryGetProperty("format", out var value) ? value.GetInt32() : 0;
        if (format != RowStore.FormatVersion)
        {
            var reason = $"stores rows in format {format}; this computer reads format {RowStore.FormatVersion}";
            throw Unreachable(reason, $"{_name} {reason}");
        }
        var mismatch = Socket.BackendBuild.Mismatch(
            welcome.TryGetProperty("backendIdentity", out var identity) && identity.ValueKind == JsonValueKind.String ? identity.GetString() : null,
            welcome.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.String ? commit.GetString() : null);
        if (path == "/socket")
        {
            _backendMismatch = mismatch;
            _bulkBackendMismatch = null;
        }
        else _bulkBackendMismatch = mismatch;
        _status.BackendMismatch(_name, _backendMismatch ?? _bulkBackendMismatch);
        return welcome.TryGetProperty("engine", out var engine) && engine.ValueKind == JsonValueKind.String ? engine.GetString() : null;
    }

    // Both helpers run only under the existing send gate; the socket reference is the connection epoch.
    private ClientWebSocket RequireEngine(string machine) => !_ownerStopping && !_socketStop.IsCancellationRequested && _socket is { State: WebSocketState.Open } socket && _engineIdentity == machine
        ? socket : throw Unreachable("unverified engine peer", $"{_name}: socket engine identity differs or is unverified");

    private void InvalidateEngine()
    {
        _ = EndSessionPublications();
        InvalidateOwnerBinding();
        _engineIdentity = null;
        _bulkEngineIdentity = null;
        _bulkSocket?.Abort();
    }

    private static Task SendFrameAsync(ClientWebSocket socket, string frame, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, cancellationToken);

    private static async Task<JsonElement> ReadFrameAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        var chunk = new byte[16384];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(chunk, cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close) throw new IOException("remote socket closed");
            bytes.Write(chunk, 0, received.Count);
            if (bytes.Length > 4 * 1024 * 1024) throw new IOException("remote frame too large");
        } while (!received.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray());
        return json.RootElement.Clone();
    }

}
