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
internal sealed partial class RemoteMachine : IMachine, IAsyncDisposable
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
        _follower = new RemoteFollower(hub, RowsAsync, ReadRowsAsync, ProbeAsync, RefreshAsync, AddSubagentSession, log);
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
        var listed = await CallAsync("machines", "agents", new MachinesFeature.AgentsArgs(), cancellationToken);
        return listed.GetProperty("agents").EnumerateArray().Select(agent => (Text(agent, "id")!, Text(agent, "name")!)).ToList();
    }

    public async Task<SessionInfo> CreateSessionAsync(string agent, string repoUrl, CancellationToken cancellationToken)
    {
        var created = await CallAsync("sessions", "create", new SessionsFeature.CreateArgs(agent, repoUrl), cancellationToken);
        return _sessionStore.Add(created.GetProperty("sessionId").GetString()!, agent, repoUrl, _name);
    }

    public async Task<SessionInfo> CreateHelperAsync(SessionInfo parent, string agent, string title, CancellationToken cancellationToken)
    {
        var created = await CallAsync("sessions", "create", new SessionsFeature.CreateArgs(agent, parent.RepoUrl, ParentSessionId: parent.SessionId, Title: title), cancellationToken);
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
        JsonElement result;
        try { result = await CallAsync("send", "send", new SendFeature.SendArgs(session.SessionId, text, clientId), cancellationToken); }
        catch (CallFailed error) when (error.Message == "the running turn can't take a message yet") { return null; }
        var runId = result.GetProperty("runId").GetString();
        _follower.Watch(session.SessionId, runId);
        return runId;
    }

    public async Task<bool> StopAsync(SessionInfo session, string runId, CancellationToken cancellationToken)
    {
        try { return (await CallAsync("send", "stop", new SendFeature.StopArgs(runId), cancellationToken)).GetProperty("ok").GetBoolean(); }
        catch (CallFailed error) when (error.Message == "that run isn't running") { return false; }
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
        ReadConfig(await CallAsync("config", "get", new ConfigFeature.GetArgs(session.SessionId), cancellationToken));

    public async Task<SessionConfig> SetConfigAsync(SessionInfo session, string configId, string value, CancellationToken cancellationToken) =>
        ReadConfig(await CallAsync("config", "set", new ConfigFeature.SetArgs(session.SessionId, configId, value), cancellationToken));

    public async Task<bool> AnswerAsync(SessionInfo session, string askId, JsonElement answer, CancellationToken cancellationToken)
    {
        try { return (await CallAsync("send", "answer", new SendFeature.AnswerArgs(session.SessionId, askId, answer), cancellationToken)).GetProperty("ok").GetBoolean(); }
        // SendFeature.cs rejects an expired ask with this failed message.
        catch (CallFailed error) when (error.Message == "that ask isn't open, or the answer doesn't fit it") { return false; }
    }

    public async Task<bool> StopTaskAsync(SessionInfo session, string taskId, CancellationToken cancellationToken)
    {
        try { return (await CallAsync("send", "stopTask", new SendFeature.StopTaskArgs(session.SessionId, taskId), cancellationToken)).GetProperty("ok").GetBoolean(); }
        catch (CallFailed error) when (error.Message == "that task isn't running, or can't be stopped") { return false; }
    }

    private static SessionConfig ReadConfig(JsonElement config) =>
        new(config.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array ? options.GetRawText() : null,
            SessionStore.ReadChoices(config.GetProperty("choices").GetRawText()),
            config.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array ? commands.GetRawText() : null);

    public void Watch(SessionInfo session) => _follower.Watch(session.SessionId);

    /// <summary>A turn delivered through the Board socket still needs its rows copied here.</summary>
    internal void WatchRun(string sessionId, string runId) => _follower.Watch(sessionId, runId);

    /// <summary>Follows the remote sessions whose copied rows show a turn still running, as after a desktop restart mid-turn.</summary>
    public void WatchRunning(IEnumerable<string> sessionIds, IEnumerable<string> helpersShownRunning)
    {
        _follower.ShownRunning(helpersShownRunning);
        foreach (var sessionId in sessionIds.Concat(helpersShownRunning).Distinct())
            _follower.Watch(sessionId);
    }

    /// <summary>Opens the machine socket and checks the welcome before marking it reachable.</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        await ConnectSocketAsync(cancellationToken);
        await _socketSend.WaitAsync(cancellationToken);
        try { _socketRun ??= Task.Run(SocketLoopAsync, CancellationToken.None); }
        finally { _socketSend.Release(); }
        _status.Reachable(_name);
        _follower.Resume();
    }

    private Task RefreshAsync(string sessionId)
    {
        _turnEnded(sessionId);
        return Task.CompletedTask;
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
