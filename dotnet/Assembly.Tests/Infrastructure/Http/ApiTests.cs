using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Mcp;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>The real server on a free port, with a scripted agent behind it.</summary>
public sealed class ApiTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly TempDatabase _db = new();
    private readonly string _work = Directory.CreateTempSubdirectory("assembly-api-").FullName;
    private readonly FakeLauncher _launcher = new();
    private AppDatabase _database = null!;
    private RowStore _store = null!;
    private LiveHub _hub = null!;
    private SessionStore _sessionStore = null!;
    private SessionRegistry _sessions = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private Uri _base = null!;

    public async Task InitializeAsync()
    {
        _database = TestDatabase.Open(_db.Path);
        _store = RowStore.Open(_database);
        _hub = new LiveHub(_store);
        _sessionStore = SessionStore.Open(_database, TimeProvider.System);
        _sessions = new SessionRegistry(_hub, _sessionStore, _launcher, TimeProvider.System, TextWriter.Null);
        var attachments = new AttachmentStore(Path.Combine(_work, "attachments"));
        var machines = new MachineSet(new LocalMachine(_hub, _sessionStore, _sessions, attachments));
        _app = ApiServer.Build(new ApiServices(_hub, _sessionStore, machines, attachments, Token), port: 0, out _);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _base = new Uri(address);
        _http = new HttpClient { BaseAddress = _base };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    [Fact]
    public void The_host_stops_within_its_shutdown_timeout()
    {
        // scripts/shutdown-timing.test.sh checks the constant against the shell's wait; this checks the host uses it.
        Assert.Equal(ApiServer.ShutdownTimeout, _app.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    [Fact]
    public async Task Every_route_needs_the_token_and_the_query_token_works_only_on_the_socket()
    {
        using var anonymous = new HttpClient { BaseAddress = _base };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/sessions?token={Token}")).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/sessions")).StatusCode);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(SocketUri($"/live?session=x&after=0&token={Token}"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);
        using var refused = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => refused.ConnectAsync(SocketUri("/live?session=x&after=0&token=wrong"), CancellationToken.None));
    }

    [Fact]
    public async Task The_webview_origins_get_cors_and_preflights_need_no_token()
    {
        using var anonymous = new HttpClient { BaseAddress = _base };
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/sessions/x/send");
        preflight.Headers.Add("Origin", "tauri://localhost");
        var answer = await anonymous.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Equal("tauri://localhost", answer.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("Authorization, Content-Type", answer.Headers.GetValues("Access-Control-Allow-Headers").Single());

        using var other = new HttpRequestMessage(HttpMethod.Get, "/sessions");
        other.Headers.Add("Origin", "https://example.com");
        var listed = await _http.SendAsync(other);
        Assert.False(listed.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("no-store", listed.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_session_runs_the_agent_it_was_made_with()
    {
        var agents = (await GetJsonAsync("/agents")).GetProperty("agents").EnumerateArray()
            .Select(agent => (agent.GetProperty("id").GetString(), agent.GetProperty("name").GetString()));
        Assert.Equal([("claude", "Claude Code"), ("codex", "Codex")], agents);

        var created = await PostJsonAsync("/sessions", """{"agent":"codex","repoUrl":"https://github.com/o/r"}""");
        var sessionId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString()!;
        Assert.Equal("codex", (await GetJsonAsync($"/sessions/{sessionId}")).GetProperty("agent").GetString());
        await SendAsync(sessionId, "hi");
        await _launcher.NextAsync();

        Assert.Equal(["codex"], _launcher.Launched);
    }

    [Fact]
    public async Task A_session_s_config_starts_from_its_agent_s_and_keeps_what_is_chosen()
    {
        var sessionId = await CreateSessionAsync();
        var empty = await GetJsonAsync($"/sessions/{sessionId}/config");
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("options").ValueKind);

        _sessionStore.SetAgentConfig("claude", """[{"id":"model","type":"select","currentValue":"opus","options":[{"value":"opus"},{"value":"haiku"}]}]""");
        var chosen = await PostJsonAsync($"/sessions/{sessionId}/config", """{"configId":"model","value":"haiku"}""");
        var config = JsonDocument.Parse(await chosen.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("opus", config.GetProperty("options")[0].GetProperty("currentValue").GetString());
        Assert.Equal("haiku", config.GetProperty("choices").GetProperty("model").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await PostJsonAsync($"/sessions/{sessionId}/config", """{"value":"x"}""")).StatusCode);
    }

    [Fact]
    public async Task Sessions_are_created_and_listed_newest_first()
    {
        var first = await CreateSessionAsync();
        var second = await CreateSessionAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await PostJsonAsync("/sessions", """{"agent":"gemini","repoUrl":"x"}""")).StatusCode);

        var list = await GetJsonAsync("/sessions");
        var sessions = list.GetProperty("sessions").EnumerateArray().ToList();
        Assert.Equal([second, first], sessions.Select(t => t.GetProperty("sessionId").GetString()));
        Assert.Equal("claude", sessions[0].GetProperty("agent").GetString());
        Assert.Equal("https://github.com/o/r", sessions[0].GetProperty("repoUrl").GetString());
        Assert.Equal(JsonValueKind.Null, sessions[0].GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, sessions[0].GetProperty("machine").ValueKind);
        Assert.True(sessions[0].GetProperty("updatedAt").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, list.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task One_session_is_read_by_id()
    {
        var sessionId = await CreateSessionAsync();
        _sessionStore.SetTitle(sessionId, "Fix the build");

        var session = await GetJsonAsync($"/sessions/{sessionId}");
        Assert.Equal(sessionId, session.GetProperty("sessionId").GetString());
        Assert.Equal("Fix the build", session.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/sessions/nope")).StatusCode);
    }

    [Fact]
    public async Task The_row_format_and_the_machines_are_reported()
    {
        // The desktop copies a box's rows only when their row formats match.
        Assert.Equal(RowStore.FormatVersion, (await GetJsonAsync("/version")).GetProperty("formatVersion").GetInt32());
        Assert.Empty((await GetJsonAsync("/machines")).GetProperty("machines").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PostJsonAsync("/sessions", """{"agent":"claude","repoUrl":"r","machine":"nope"}""")).StatusCode);
    }

    [Fact]
    public async Task A_sent_turn_shows_on_the_socket_and_in_pages_and_a_second_send_waits()
    {
        var sessionId = await CreateSessionAsync();
        using var socket = await OpenLiveAsync(sessionId, after: 0);

        var runId = (await SendAsync(sessionId, "hi")).GetProperty("runId").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await PostJsonAsync($"/sessions/{sessionId}/send", """{"text":"again"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostJsonAsync("/sessions/nope/send", """{"text":"hi"}""")).StatusCode);

        var page = await GetJsonAsync($"/sessions/{sessionId}/rows");
        Assert.Equal(runId, page.GetProperty("activeRunId").GetString());
        Assert.False(page.GetProperty("hasBefore").GetBoolean());
        Assert.Equal(["user", "turn"], page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("kind").GetString()));

        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.TextAsync("Hello");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");

        var rows = await ReadRowsAsync(socket, 6);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], rows.Select(row => row.GetProperty("seq").GetInt64()));
        Assert.Equal(["user", "turn", "delta", "message", "stats", "turn"], rows.Select(row => row.GetProperty("kind").GetString()));
        Assert.Equal(sessionId, rows[0].GetProperty("sessionId").GetString());
        Assert.Equal(runId, rows[5].GetProperty("runId").GetString());

        var after = await GetJsonAsync($"/sessions/{sessionId}/rows");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("activeRunId").ValueKind);
        Assert.Equal(["user", "turn", "message", "stats", "turn"], after.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task A_socket_reopened_mid_turn_carries_on_from_its_watermark()
    {
        var sessionId = await CreateSessionAsync();
        await SendAsync(sessionId, "hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");

        using (var first = await OpenLiveAsync(sessionId, after: 0))
        {
            Assert.Equal([1L, 2L], (await ReadRowsAsync(first, 2)).Select(row => row.GetProperty("seq").GetInt64()));
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }

        await agent.TextAsync("Hello");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        using var second = await OpenLiveAsync(sessionId, after: 2);
        AssertCaughtUpFrom(2, await ReadUntilTurnEndsAsync(second));
    }

    [Fact]
    public async Task An_opened_tool_row_gets_its_detail()
    {
        var sessionId = await CreateSessionAsync();
        await SendAsync(sessionId, "list files");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"Terminal","kind":"execute","status":"pending","rawInput":{"command":"ls"}}""");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"t1","status":"completed","rawOutput":"a.txt"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        using var socket = await OpenLiveAsync(sessionId, after: 0);
        await ReadRowsAsync(socket, 6);

        var detail = await GetJsonAsync($"/sessions/{sessionId}/tools/t1");
        Assert.Equal("ls", detail.GetProperty("summary").GetString());
        Assert.Equal("""{"command":"ls"}""", detail.GetProperty("input").GetString());
        Assert.Equal("a.txt", detail.GetProperty("output").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("diff").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/sessions/{sessionId}/tools/nope")).StatusCode);
    }

    [Fact]
    public async Task Stop_cancels_the_run_it_names()
    {
        var sessionId = await CreateSessionAsync();
        var runId = (await SendAsync(sessionId, "hi")).GetProperty("runId").GetString();
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");

        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/runs/nope/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsync($"/runs/{runId}/stop", null)).StatusCode);
        await agent.ReadAsync("session/cancel");
        await agent.ReplyAsync(prompt, """{"stopReason":"cancelled"}""");

        using var socket = await OpenLiveAsync(sessionId, after: 0);
        var rows = await ReadRowsAsync(socket, 4);
        Assert.Equal("cancelled", rows[3].GetProperty("payload").GetProperty("state").GetString());
    }

    [Fact]
    public async Task An_image_is_stored_by_path_and_read_back_by_name()
    {
        var sessionId = await CreateSessionAsync();
        using var body = new ByteArrayContent(Png);
        body.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var saved = await _http.PostAsync($"/sessions/{sessionId}/attachments", body);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var path = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement.GetProperty("path").GetString()!;

        Assert.StartsWith(Path.Combine(_work, "attachments", sessionId), path);
        Assert.Equal(Png, await File.ReadAllBytesAsync(path));
        var read = await _http.GetAsync($"/sessions/{sessionId}/attachments/{Path.GetFileName(path)}");
        Assert.Equal("image/png", read.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await read.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Only_real_images_are_stored_and_only_their_own_names_are_served()
    {
        var sessionId = await CreateSessionAsync();
        using var lying = new ByteArrayContent("not a png"u8.ToArray());
        lying.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _http.PostAsync($"/sessions/{sessionId}/attachments", lying)).StatusCode);
        using var text = new StringContent("hello", Encoding.UTF8, "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _http.PostAsync($"/sessions/{sessionId}/attachments", text)).StatusCode);
        using var unknown = new ByteArrayContent(Png);
        unknown.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/sessions/nope/attachments", unknown)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync($"/sessions/{sessionId}/attachments/..%2F..%2Fassembly.db")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/sessions/{sessionId}/attachments/{Guid.NewGuid()}.png")).StatusCode);
    }

    private async Task<string> CreateSessionAsync()
    {
        var created = await PostJsonAsync("/sessions", """{"agent":"claude","repoUrl":"https://github.com/o/r"}""");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task<JsonElement> SendAsync(string sessionId, string text)
    {
        var sent = await PostJsonAsync($"/sessions/{sessionId}/send", JsonSerializer.Serialize(new { text }));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        return JsonDocument.Parse(await sent.Content.ReadAsStringAsync()).RootElement;
    }

    private Task<HttpResponseMessage> PostJsonAsync(string path, string json) =>
        _http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var answer = await _http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement;
    }

    private Uri SocketUri(string pathAndQuery) => new($"ws://{_base.Authority}{pathAndQuery}");

    private async Task<ClientWebSocket> OpenLiveAsync(string sessionId, long after)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(SocketUri($"/live?session={sessionId}&after={after}&token={Token}"), CancellationToken.None);
        return socket;
    }

    /// <summary>
    /// Rows read from a watermark: each one's prevSeq is the seq before it, and the turn ended. The delta is there only
    /// if the socket opened before its message finished, so it's left out of the kinds.
    /// </summary>
    internal static void AssertCaughtUpFrom(long watermark, List<JsonElement> rows)
    {
        Assert.Equal([watermark, .. rows.SkipLast(1).Select(row => row.GetProperty("seq").GetInt64())],
            rows.Select(row => row.GetProperty("prevSeq").GetInt64()));
        Assert.Equal(["message", "stats", "turn"], rows.Select(row => row.GetProperty("kind").GetString()).Where(kind => kind != "delta"));
    }

    /// <summary>Row frames up to and including the turn row that ends the run.</summary>
    internal static async Task<List<JsonElement>> ReadUntilTurnEndsAsync(ClientWebSocket socket)
    {
        var rows = new List<JsonElement>();
        do
            rows.AddRange(await ReadRowsAsync(socket, 1));
        while (rows[^1].GetProperty("kind").GetString() != "turn" || rows[^1].GetProperty("payload").GetProperty("state").GetString() == "started");
        return rows;
    }

    /// <summary>The next <paramref name="count"/> row frames. Pings are skipped.</summary>
    internal static async Task<List<JsonElement>> ReadRowsAsync(ClientWebSocket socket, int count)
    {
        using var deadline = new CancellationTokenSource(Wait);
        var rows = new List<JsonElement>();
        var buffer = new byte[64 * 1024];
        while (rows.Count < count)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, deadline.Token);
                message.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);
            var frame = JsonDocument.Parse(message.ToArray()).RootElement;
            if (frame.GetProperty("type").GetString() == "row")
                rows.Add(frame.GetProperty("row").Clone());
        }

        return rows;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _sessions.DisposeAsync();
        _hub.Dispose();
        _database.Dispose();
        _db.Dispose();
        Directory.Delete(_work, recursive: true);
    }
}
