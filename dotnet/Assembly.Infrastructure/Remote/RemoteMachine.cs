using System.Net;
using System.Net.WebSockets;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Features.Chat.Helpers;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Infrastructure.Remote;

/// <summary>
/// A machine whose agent runs elsewhere, reached at <paramref name="address"/> with its own token: a Assembly.Remote
/// serve on the same engine, which converts and stores its own rows. Creating, sending, stopping, tool details and
/// attachments are its routes; its rows are copied into the desktop's store (<see cref="RemoteFollower"/>), which the
/// webview reads like any other session's.
/// </summary>
internal sealed class RemoteMachine : IMachine, IAsyncDisposable
{
    private readonly string _name;
    private readonly LiveHub _hub;
    private readonly SessionStore _sessionStore;
    private readonly HttpClient _http;
    private readonly RemoteFollower _follower;
    private readonly Action<string> _turnEnded;
    private readonly MachineStatus _status;
    private bool _formatChecked;

    /// <param name="turnEnded">Told when a copied turn ends (a helper's row goes idle).</param>
    public RemoteMachine(string name, Uri address, string token, LiveHub hub, SessionStore sessionStore, TextWriter log, MachineStatus status,
        Action<string>? turnEnded = null, TimeProvider? time = null)
    {
        _address = address;
        _token = token;
        _time = time ?? TimeProvider.System;
        _log = log;
        _turnEnded = turnEnded ?? (_ => { });
        _name = name;
        _hub = hub;
        _sessionStore = sessionStore;
        _status = status;
        _http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _follower = new RemoteFollower(hub, RowsAsync, ReadRowsAsync, CheckFormatAsync, RefreshAsync, AddSubagentSession, log);
    }

    public string? Name => _name;

    internal string[] FollowedSessions => _follower.FollowedSessions;

    // A subagent's session, made on the remote by its parent's session, is known here from its parent's row.
    private void AddSubagentSession(string parentSessionId, StoredRow row)
    {
        if (row.Payload.GetProperty("sessionId").GetString() is not { } childId || _sessionStore.Get(childId) is not null
            || _sessionStore.Get(parentSessionId) is not { } parent)
            return;
        var agent = row.Payload.TryGetProperty("agent", out var named) && named.GetString() is { } helperAgent ? helperAgent : parent.Agent;
        _sessionStore.Add(childId, agent, parent.RepoUrl, _name, parentSessionId,
            helper: row.Payload.TryGetProperty("helper", out var helper) && helper.ValueKind == JsonValueKind.True);
        if (row.Payload.GetProperty("title").GetString() is { } title)
            _sessionStore.SetTitle(childId, title, "prompt");
    }

    public async Task<IReadOnlyList<(string Id, string Name)>> AgentsAsync(CancellationToken cancellationToken)
    {
        await CheckFormatAsync(cancellationToken);
        var listed = await JsonAsync(HttpMethod.Get, "/agents", null, cancellationToken);
        return listed.GetProperty("agents").EnumerateArray().Select(agent => (Text(agent, "id")!, Text(agent, "name")!)).ToList();
    }

    public async Task<SessionInfo> CreateSessionAsync(string agent, string repoUrl, CancellationToken cancellationToken)
    {
        await CheckFormatAsync(cancellationToken);
        var created = await JsonAsync(HttpMethod.Post, "/sessions", Body(("agent", agent), ("repoUrl", repoUrl)), cancellationToken);
        return _sessionStore.Add(created.GetProperty("sessionId").GetString()!, agent, repoUrl, _name);
    }

    public async Task<SessionInfo> CreateHelperAsync(SessionInfo parent, string agent, string title, CancellationToken cancellationToken)
    {
        await CheckFormatAsync(cancellationToken);
        var created = await JsonAsync(HttpMethod.Post, "/sessions",
            Body(("agent", agent), ("repoUrl", parent.RepoUrl), ("parentSessionId", parent.SessionId), ("title", title)), cancellationToken);
        var sessionId = created.GetProperty("sessionId").GetString()!;
        _sessionStore.Add(sessionId, agent, parent.RepoUrl, _name, parent.SessionId, helper: true);
        _sessionStore.SetTitle(sessionId, title, "prompt");
        return _sessionStore.Get(sessionId)!;
    }

    /// <summary>The messages the remote's sessions have waiting for sessions here.</summary>
    public async Task<IReadOnlyList<HelperMessage>> OutboxAsync(CancellationToken cancellationToken) =>
        (await JsonAsync(HttpMethod.Get, "/outbox", null, cancellationToken)).GetProperty("messages").EnumerateArray()
            .Select(message => new HelperMessage(Text(message, "messageId")!, Text(message, "from")!, Text(message, "to")!, Text(message, "text")!, null,
                message.TryGetProperty("createdAt", out var at) ? at.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ToList();

    /// <summary>Tells the remote how delivering its message went: the run it went into, or why it couldn't.</summary>
    public async Task DeliveredAsync(string messageId, string? runId, string? error, CancellationToken cancellationToken) =>
        await JsonAsync(HttpMethod.Post, $"/outbox/{Uri.EscapeDataString(messageId)}",
            runId is not null ? Body(("runId", runId)) : Body(("error", error ?? "It couldn't be delivered.")), cancellationToken);

    /// <summary>Brings the remote the answer to its message.</summary>
    public async Task AnsweredAsync(string messageId, RunAnswer answer, CancellationToken cancellationToken) =>
        await JsonAsync(HttpMethod.Post, $"/outbox/{Uri.EscapeDataString(messageId)}/answer",
            answer.Error is { } error
                ? Body(("state", answer.State), ("reply", answer.Reply), ("error", error))
                : Body(("state", answer.State), ("reply", answer.Reply)), cancellationToken);

    public async Task<string?> SendAsync(SessionInfo session, string text, string? clientId, CancellationToken cancellationToken)
    {
        // Before the send, so a catch-up settling meanwhile can't end the row of the turn it starts; a send that fails
        // leaves the row as it is.
        _follower.TakeShownRunning(session.SessionId);
        using var response = await SendRequestAsync(HttpMethod.Post, $"/sessions/{session.SessionId}/send",
            clientId is null ? Body(("text", text)) : Body(("text", text), ("clientId", clientId)), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return null;
        var runId = (await ReadJsonAsync(response, cancellationToken)).GetProperty("runId").GetString();
        _follower.Watch(session.SessionId);
        return runId;
    }

    public async Task<bool> StopAsync(SessionInfo session, string runId, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(HttpMethod.Post, $"/runs/{runId}/stop", null, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NotFound)
            return response.StatusCode == HttpStatusCode.Accepted;
        // Any other answer fails the call, a 200 included.
        throw new CallFailed($"{_name} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
    }

    // From the copied rows, so it agrees with what the webview has.
    public string? ActiveRun(SessionInfo session) => _hub.OpenRun(session.SessionId);

    public async Task<StoredToolCall?> ToolCallAsync(SessionInfo session, string itemId, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(HttpMethod.Get, $"/sessions/{session.SessionId}/tools/{Uri.EscapeDataString(itemId)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var call = await ReadJsonAsync(response, cancellationToken);
        return new StoredToolCall(Text(call, "summary"), Text(call, "input"), Text(call, "output"), Text(call, "diff"), Text(call, "path"));
    }

    public async Task<string> SaveAttachmentAsync(SessionInfo session, ImageType type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var content = new ReadOnlyMemoryContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type.ContentType);
        var saved = await JsonAsync(HttpMethod.Post, $"/sessions/{session.SessionId}/attachments", content, cancellationToken);
        return saved.GetProperty("path").GetString()!;
    }

    public async Task<SessionConfig> ConfigAsync(SessionInfo session, CancellationToken cancellationToken) =>
        ReadConfig(await JsonAsync(HttpMethod.Get, $"/sessions/{session.SessionId}/config", null, cancellationToken));

    public async Task<SessionConfig> SetConfigAsync(SessionInfo session, string configId, string value, CancellationToken cancellationToken) =>
        ReadConfig(await JsonAsync(HttpMethod.Post, $"/sessions/{session.SessionId}/config", Body(("configId", configId), ("value", value)), cancellationToken));

    public async Task<bool> AnswerAsync(SessionInfo session, string askId, JsonElement answer, CancellationToken cancellationToken)
    {
        using var content = new StringContent(answer.GetRawText(), Encoding.UTF8, "application/json");
        using var response = await SendRequestAsync(HttpMethod.Post, $"/sessions/{session.SessionId}/asks/{Uri.EscapeDataString(askId)}", content, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return false;
        await ReadJsonAsync(response, cancellationToken);
        return true;
    }

    public async Task<bool> StopTaskAsync(SessionInfo session, string taskId, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(HttpMethod.Post,
            $"/sessions/{session.SessionId}/tasks/{Uri.EscapeDataString(taskId)}/stop", content: null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return false;
        await ReadJsonAsync(response, cancellationToken);
        return true;
    }

    private static SessionConfig ReadConfig(JsonElement config) =>
        new(config.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array ? options.GetRawText() : null,
            SessionStore.ReadChoices(config.GetProperty("choices").GetRawText()),
            config.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array ? commands.GetRawText() : null);

    public void Watch(SessionInfo session) => _follower.Watch(session.SessionId);

    /// <summary>Follows the remote sessions whose copied rows show a turn still running, as after a desktop restart mid-turn.</summary>
    public void WatchRunning(IEnumerable<string> sessionIds, IEnumerable<string> helpersShownRunning)
    {
        _follower.ShownRunning(helpersShownRunning);
        foreach (var sessionId in sessionIds.Concat(helpersShownRunning).Distinct())
            _follower.Watch(sessionId);
    }

    /// <summary>
    /// Reads the remote's row format again, so a remote upgraded in place is seen, unless an open follower link already
    /// shows it's there. Either way, the sessions the follower gave up on while it was unreachable are followed again.
    /// </summary>
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        if (_socket is { State: WebSocketState.Open })
        {
            _status.Reachable(_name);
            _follower.Resume();
            return;
        }
        _formatChecked = false;
        await CheckFormatAsync(cancellationToken);
        _follower.Resume();
    }

    // The remote's rows are copied as they are, so it must write the format this computer reads. Until it's known to,
    // an answer doesn't mark it reachable, so another format never shows reachable first.
    private async Task CheckFormatAsync(CancellationToken cancellationToken)
    {
        if (_formatChecked)
            return;
        var version = (await JsonAsync(HttpMethod.Get, "/version", null, cancellationToken)).GetProperty("formatVersion").GetInt32();
        if (version != RowStore.FormatVersion)
        {
            var reason = $"stores rows in format {version}; this computer reads format {RowStore.FormatVersion}";
            throw Unreachable(reason, $"{_name} {reason}");
        }
        _formatChecked = true;
        _status.Reachable(_name);
    }

    // A finished turn may have named the session on the remote, its utility model too. A title the user gave here is
    // kept, and SetTitle keeps a helper title held here over a prompt one from the remote.
    private async Task RefreshAsync(string sessionId)
    {
        _turnEnded(sessionId);
        try
        {
            var session = await JsonAsync(HttpMethod.Get, $"/sessions/{sessionId}", null, CancellationToken.None);
            if (Text(session, "title") is { } title && _sessionStore.Get(sessionId) is { Helper: false, TitleSource: not "user" })
                _sessionStore.SetTitle(sessionId, title, Text(session, "titleSource") ?? "prompt");
            _sessionStore.Touch(sessionId);
        }
        catch (Exception error) when (error is MachineUnreachable or CallFailed)
        {
            // The title can wait for the next turn.
        }
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(method, path, content, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    // Any answer means the remote is there, unless it refused this computer's token: then nothing will work.
    private async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Unreachable("didn't answer", $"{_name} didn't answer: {error.Message}", error);
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw Unreachable("the remote refused this computer's token", $"{_name}: the remote refused this computer's token");
        }
        if (_formatChecked)
            _status.Reachable(_name);
        return response;
    }

    // Recorded before it's thrown.
    private MachineUnreachable Unreachable(string reason, string message, Exception? inner = null)
    {
        _status.Unreachable(_name, reason);
        return new MachineUnreachable(_name, message, inner);
    }

    // The remote answered, so a failure here is the call's, not the machine's.
    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CallFailed($"{_name} answered {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private static StringContent Body(params (string Name, string Value)[] fields)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            foreach (var (name, value) in fields)
                json.WriteString(name, value);
            json.WriteEndObject();
        }
        return new StringContent(Encoding.UTF8.GetString(stream.ToArray()), Encoding.UTF8, "application/json");
    }

    private static string? Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // All followed sessions share this receive loop. A backend restart can leave SSH up, so this machine owns retry.
    private readonly CancellationTokenSource _socketStop = new();
    private readonly SemaphoreSlim _socketSend = new(1), _bulkCall = new(1);
    private readonly Dictionary<long, (string Session, System.Threading.Channels.Channel<JsonElement> Frames)> _subscriptions = [];
    private ClientWebSocket? _socket, _bulkSocket;
    private Task? _socketRun;
    private long _nextSub;
    private readonly Uri _address = null!;
    private readonly string _token = null!;
    private readonly TimeProvider _time = null!;
    private readonly TextWriter _log = null!;

    private async IAsyncEnumerable<JsonElement> RowsAsync(string session, long after,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var frames = System.Threading.Channels.Channel.CreateBounded<JsonElement>(512);
        var id = Interlocked.Increment(ref _nextSub);
        await _socketSend.WaitAsync(cancellationToken);
        try
        {
            _subscriptions.Add(id, (session, frames));
            _socketRun ??= Task.Run(SocketLoopAsync, CancellationToken.None);
            if (_socket is { State: WebSocketState.Open } socket)
                await SendFrameAsync(socket, SubFrame(id, session, after), cancellationToken);
        }
        catch
        {
            _subscriptions.Remove(id);
            _socket?.Abort();
            throw;
        }
        finally { _socketSend.Release(); }
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken))
                yield return frame;
        }
        finally
        {
            await _socketSend.WaitAsync(CancellationToken.None);
            try
            {
                _subscriptions.Remove(id);
                if (_socket is { State: WebSocketState.Open } socket)
                    try { await SendFrameAsync(socket, $"{{\"t\":\"unsub\",\"id\":{id}}}", _socketStop.Token); }
                    catch (Exception error) when (error is WebSocketException or OperationCanceledException) { socket.Abort(); }
            }
            finally { _socketSend.Release(); }
        }
    }

    private static string SubFrame(long id, string session, long after) =>
        $"{{\"t\":\"sub\",\"id\":{id},\"topic\":\"rows\",\"key\":{JsonSerializer.Serialize(session, RowJsonContext.Rows.String)},\"after\":{after}}}";

    private async Task SocketLoopAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!_socketStop.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            var copied = false;
            try
            {
                await OpenSocketAsync(socket, "/socket", _socketStop.Token);
                await _socketSend.WaitAsync(_socketStop.Token);
                try
                {
                    _socket = socket;
                    foreach (var (id, sub) in _subscriptions)
                        await SendFrameAsync(socket, SubFrame(id, sub.Session, _hub.LastSeq(sub.Session)), _socketStop.Token);
                }
                finally { _socketSend.Release(); }
                _status.Reachable(_name);
                _follower.Resume();
                while (true)
                {
                    var frame = await ReadFrameAsync(socket, _socketStop.Token);
                    await _socketSend.WaitAsync(_socketStop.Token);
                    try
                    {
                        if (frame.TryGetProperty("sub", out var sub) && _subscriptions.TryGetValue(sub.GetInt64(), out var target)
                            && !target.Frames.Writer.TryWrite(frame))
                            throw new IOException("remote rows queue is full");
                    }
                    finally { _socketSend.Release(); }
                    copied |= frame.GetProperty("t").GetString() == "rows";
                }
            }
            catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException)
            {
                if (_socketStop.IsCancellationRequested) break;
                _status.Unreachable(_name, "machine socket closed");
                _log.WriteLine($"remote {_name}: socket lost ({error.GetType().Name})");
            }
            finally
            {
                await _socketSend.WaitAsync(CancellationToken.None);
                try { _socket = null; }
                finally { _socketSend.Release(); }
            }
            if (copied) backoff = TimeSpan.FromSeconds(1);
            try { await Task.Delay(backoff, _time, _socketStop.Token); }
            catch (OperationCanceledException) when (_socketStop.IsCancellationRequested) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    private async Task<IReadOnlyList<StoredRow>> ReadRowsAsync(string session, long after, CancellationToken cancellationToken)
    {
        await _bulkCall.WaitAsync(cancellationToken);
        try
        {
            if (_bulkSocket is not { State: WebSocketState.Open })
            {
                _bulkSocket?.Dispose();
                _bulkSocket = new ClientWebSocket();
                await OpenSocketAsync(_bulkSocket, "/socket/bulk", cancellationToken);
            }
            await SendFrameAsync(_bulkSocket,
                $"{{\"t\":\"call\",\"id\":1,\"feature\":\"rows\",\"method\":\"after\",\"args\":{{\"session\":{JsonSerializer.Serialize(session, RowJsonContext.Rows.String)},\"after\":{after},\"limit\":{RowsFeature.PageLimit}}}}}", cancellationToken);
            var frame = await ReadFrameAsync(_bulkSocket, cancellationToken);
            if (frame.GetProperty("t").GetString() != "result") throw new IOException("remote rows read failed");
            return frame.GetProperty("result").GetProperty("rows").EnumerateArray().Select(RowWire.Read).ToArray();
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested) _socket?.Abort();
            _bulkSocket?.Dispose();
            _bulkSocket = null;
            throw;
        }
        finally { _bulkCall.Release(); }
    }

    private async Task OpenSocketAsync(ClientWebSocket socket, string path, CancellationToken cancellationToken)
    {
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_token}");
        var uri = new UriBuilder(_address) { Scheme = _address.Scheme == "https" ? "wss" : "ws", Path = path };
        await socket.ConnectAsync(uri.Uri, cancellationToken);
        await SendFrameAsync(socket, $"{{\"t\":\"hello\",\"protocol\":1,\"token\":{JsonSerializer.Serialize(_token, RowJsonContext.Rows.String)}}}", cancellationToken);
        var welcome = await ReadFrameAsync(socket, cancellationToken);
        if (welcome.GetProperty("t").GetString() != "welcome" || welcome.GetProperty("protocol").GetInt32() != 1)
            throw new IOException("remote socket handshake failed");
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

    public async ValueTask DisposeAsync()
    {
        await _socketStop.CancelAsync();
        await _follower.DisposeAsync();
        if (_socketRun is not null) await _socketRun;
        _bulkSocket?.Dispose();
        _socketStop.Dispose();
        _socketSend.Dispose();
        _bulkCall.Dispose();
        _http.Dispose();
    }
}
