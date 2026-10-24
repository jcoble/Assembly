using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Attachments;
using Assembly.Engine.Helpers;
using Assembly.Engine.Live;
using Assembly.Engine.Machines;
using Assembly.Engine.Store;
using Assembly.Engine.Threads;

namespace Assembly.Mac.Remote;

/// <summary>
/// A machine whose agent runs elsewhere, reached at <paramref name="address"/> with its own token: a Assembly.Remote
/// serve on the same engine, which converts and stores its own rows. Creating, sending, stopping, tool details and
/// attachments are its routes; its rows are copied into the Mac's store (<see cref="RemoteFollower"/>), which the
/// webview reads like any other thread's.
/// </summary>
internal sealed class RemoteMachine : IMachine, IAsyncDisposable
{
    private readonly string _name;
    private readonly LiveHub _hub;
    private readonly ThreadStore _threads;
    private readonly HttpClient _http;
    private readonly RemoteFollower _follower;
    private readonly Action<string> _turnEnded;
    private bool _formatChecked;

    /// <param name="turnEnded">Told when a copied turn ends (a helper's row goes idle).</param>
    public RemoteMachine(string name, Uri address, string token, LiveHub hub, ThreadStore threads, TextWriter log, Action<string>? turnEnded = null)
    {
        _turnEnded = turnEnded ?? (_ => { });
        _name = name;
        _hub = hub;
        _threads = threads;
        _http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _follower = new RemoteFollower(address, token, hub, CheckFormatAsync, RefreshAsync, AddSubagentThread, log);
    }

    public string? Name => _name;

    // A subagent's thread, made on the remote by its parent's session, is known here from its parent's row.
    private void AddSubagentThread(string parentThreadId, StoredRow row)
    {
        if (row.Payload.GetProperty("threadId").GetString() is not { } childId || _threads.Get(childId) is not null
            || _threads.Get(parentThreadId) is not { } parent)
            return;
        var agent = row.Payload.TryGetProperty("agent", out var named) && named.GetString() is { } helperAgent ? helperAgent : parent.Agent;
        _threads.Add(childId, agent, parent.RepoUrl, _name, parentThreadId,
            helper: row.Payload.TryGetProperty("helper", out var helper) && helper.ValueKind == JsonValueKind.True);
        if (row.Payload.GetProperty("title").GetString() is { } title)
            _threads.SetTitle(childId, title);
    }

    public async Task<IReadOnlyList<(string Id, string Name)>> AgentsAsync(CancellationToken cancellationToken)
    {
        await CheckFormatAsync(cancellationToken);
        var listed = await JsonAsync(HttpMethod.Get, "/agents", null, cancellationToken);
        return listed.GetProperty("agents").EnumerateArray().Select(agent => (Text(agent, "id")!, Text(agent, "name")!)).ToList();
    }

    public async Task<ThreadInfo> CreateThreadAsync(string agent, string repoUrl, CancellationToken cancellationToken)
    {
        await CheckFormatAsync(cancellationToken);
        var created = await JsonAsync(HttpMethod.Post, "/threads", Body(("agent", agent), ("repoUrl", repoUrl)), cancellationToken);
        return _threads.Add(created.GetProperty("threadId").GetString()!, agent, repoUrl, _name);
    }

    public async Task<ThreadInfo> CreateHelperAsync(ThreadInfo parent, string agent, string title, CancellationToken cancellationToken)
    {
        await CheckFormatAsync(cancellationToken);
        var created = await JsonAsync(HttpMethod.Post, "/threads",
            Body(("agent", agent), ("repoUrl", parent.RepoUrl), ("parentThreadId", parent.ThreadId), ("title", title)), cancellationToken);
        var threadId = created.GetProperty("threadId").GetString()!;
        _threads.Add(threadId, agent, parent.RepoUrl, _name, parent.ThreadId, helper: true);
        _threads.SetTitle(threadId, title);
        return _threads.Get(threadId)!;
    }

    /// <summary>The messages the remote's sessions have waiting for threads here.</summary>
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

    public async Task<string?> SendAsync(ThreadInfo thread, string text, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(HttpMethod.Post, $"/threads/{thread.ThreadId}/send", Body(("text", text)), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return null;
        var runId = (await ReadJsonAsync(response, cancellationToken)).GetProperty("runId").GetString();
        _follower.Watch(thread.ThreadId);
        return runId;
    }

    public async Task<bool> StopAsync(ThreadInfo thread, string runId, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(HttpMethod.Post, $"/runs/{runId}/stop", null, cancellationToken);
        return response.StatusCode == HttpStatusCode.Accepted;
    }

    // From the copied rows, so it agrees with what the webview has.
    public string? ActiveRun(ThreadInfo thread) => _hub.OpenRun(thread.ThreadId);

    public async Task<StoredToolCall?> ToolCallAsync(ThreadInfo thread, string itemId, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(HttpMethod.Get, $"/threads/{thread.ThreadId}/tools/{Uri.EscapeDataString(itemId)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var call = await ReadJsonAsync(response, cancellationToken);
        return new StoredToolCall(Text(call, "summary"), Text(call, "input"), Text(call, "output"), Text(call, "diff"), Text(call, "path"));
    }

    public async Task<string> SaveAttachmentAsync(ThreadInfo thread, ImageType type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var content = new ReadOnlyMemoryContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type.ContentType);
        var saved = await JsonAsync(HttpMethod.Post, $"/threads/{thread.ThreadId}/attachments", content, cancellationToken);
        return saved.GetProperty("path").GetString()!;
    }

    public async Task<ThreadConfig> ConfigAsync(ThreadInfo thread, CancellationToken cancellationToken) =>
        ReadConfig(await JsonAsync(HttpMethod.Get, $"/threads/{thread.ThreadId}/config", null, cancellationToken));

    public async Task<ThreadConfig> SetConfigAsync(ThreadInfo thread, string configId, string value, CancellationToken cancellationToken) =>
        ReadConfig(await JsonAsync(HttpMethod.Post, $"/threads/{thread.ThreadId}/config", Body(("configId", configId), ("value", value)), cancellationToken));

    public async Task<bool> AnswerAsync(ThreadInfo thread, string askId, JsonElement answer, CancellationToken cancellationToken)
    {
        using var content = new StringContent(answer.GetRawText(), Encoding.UTF8, "application/json");
        using var response = await SendRequestAsync(HttpMethod.Post, $"/threads/{thread.ThreadId}/asks/{Uri.EscapeDataString(askId)}", content, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return false;
        await ReadJsonAsync(response, cancellationToken);
        return true;
    }

    public async Task<bool> StopTaskAsync(ThreadInfo thread, string taskId, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(HttpMethod.Post,
            $"/threads/{thread.ThreadId}/tasks/{Uri.EscapeDataString(taskId)}/stop", content: null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return false;
        await ReadJsonAsync(response, cancellationToken);
        return true;
    }

    private static ThreadConfig ReadConfig(JsonElement config) =>
        new(config.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array ? options.GetRawText() : null,
            ThreadConfig.ReadChoices(config.GetProperty("choices").GetRawText()),
            config.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array ? commands.GetRawText() : null);

    public void Watch(ThreadInfo thread) => _follower.Watch(thread.ThreadId);

    /// <summary>Follows the remote threads whose copied rows show a turn still running, as after a Mac restart mid-turn.</summary>
    public void WatchRunning(IEnumerable<string> threadIds)
    {
        foreach (var threadId in threadIds)
            _follower.Watch(threadId);
    }

    // The remote's rows are copied as they are, so it must write the format this Mac reads.
    private async Task CheckFormatAsync(CancellationToken cancellationToken)
    {
        if (_formatChecked)
            return;
        var version = (await JsonAsync(HttpMethod.Get, "/version", null, cancellationToken)).GetProperty("formatVersion").GetInt32();
        if (version != RowStore.FormatVersion)
            throw new MachineUnreachable($"{_name} stores rows in format {version}; this Mac reads format {RowStore.FormatVersion}");
        _formatChecked = true;
    }

    // A finished turn may have named the thread on the remote.
    private async Task RefreshAsync(string threadId)
    {
        _turnEnded(threadId);
        try
        {
            var thread = await JsonAsync(HttpMethod.Get, $"/threads/{threadId}", null, CancellationToken.None);
            if (Text(thread, "title") is { } title && _threads.Get(threadId) is { Helper: false })
                _threads.SetTitle(threadId, title);
            _threads.Touch(threadId);
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
            throw new MachineUnreachable($"{_name} didn't answer: {error.Message}", error);
        }
    }

    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new MachineUnreachable($"{_name} answered {(int)response.StatusCode}: {body}");
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
