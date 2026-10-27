using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Features.Chat.Helpers;
using Assembly.Application.Features.Chat.Rows;
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
    private bool _formatChecked;

    /// <param name="turnEnded">Told when a copied turn ends (a helper's row goes idle).</param>
    public RemoteMachine(string name, Uri address, string token, LiveHub hub, SessionStore sessionStore, TextWriter log, MachineStatus status,
        Action<string>? turnEnded = null)
    {
        _turnEnded = turnEnded ?? (_ => { });
        _name = name;
        _hub = hub;
        _sessionStore = sessionStore;
        _http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _follower = new RemoteFollower(address, token, hub, CheckFormatAsync, RefreshAsync, AddSubagentSession, log);
    }

    public string? Name => _name;

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
            _sessionStore.SetTitle(childId, title);
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
        _sessionStore.SetTitle(sessionId, title);
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

    public async Task<string?> SendAsync(SessionInfo session, string text, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(HttpMethod.Post, $"/sessions/{session.SessionId}/send", Body(("text", text)), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return null;
        var runId = (await ReadJsonAsync(response, cancellationToken)).GetProperty("runId").GetString();
        _follower.Watch(session.SessionId);
        return runId;
    }

    public async Task<bool> StopAsync(SessionInfo session, string runId, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(HttpMethod.Post, $"/runs/{runId}/stop", null, cancellationToken);
        return response.StatusCode == HttpStatusCode.Accepted;
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
    public void WatchRunning(IEnumerable<string> sessionIds)
    {
        foreach (var sessionId in sessionIds)
            _follower.Watch(sessionId);
    }

    // The remote's rows are copied as they are, so it must write the format this computer reads.
    private async Task CheckFormatAsync(CancellationToken cancellationToken)
    {
        if (_formatChecked)
            return;
        var version = (await JsonAsync(HttpMethod.Get, "/version", null, cancellationToken)).GetProperty("formatVersion").GetInt32();
        if (version != RowStore.FormatVersion)
            throw new MachineUnreachable(_name, $"{_name} stores rows in format {version}; this computer reads format {RowStore.FormatVersion}");
        _formatChecked = true;
    }

    // A finished turn may have named the session on the remote.
    private async Task RefreshAsync(string sessionId)
    {
        _turnEnded(sessionId);
        try
        {
            var session = await JsonAsync(HttpMethod.Get, $"/sessions/{sessionId}", null, CancellationToken.None);
            if (Text(session, "title") is { } title && _sessionStore.Get(sessionId) is { Helper: false })
                _sessionStore.SetTitle(sessionId, title);
            _sessionStore.Touch(sessionId);
        }
        catch (MachineUnreachable)
        {
            // The title can wait for the next turn.
        }
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(method, path, content, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new MachineUnreachable(_name, $"{_name} didn't answer: {error.Message}", error);
        }
    }

    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new MachineUnreachable(_name, $"{_name} answered {(int)response.StatusCode}: {body}");
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
        await _follower.DisposeAsync();
        _http.Dispose();
    }
}
