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
using Microsoft.Extensions.Time.Testing;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Needs;
using Assembly.Engine.Mcp;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>The real server on a free port, with a scripted agent behind it.</summary>
public sealed partial class ApiTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly TempDatabase _db = new();
    private readonly string _work = Directory.CreateTempSubdirectory("assembly-api-").FullName;
    private readonly FakeLauncher _launcher = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private AppDatabase _database = null!;
    private MachinesFeature _machinesFeature = null!;
    private LiveHub _hub = null!;
    private SessionStore _sessionStore = null!;
    private SessionRegistry _sessions = null!;
    private EngineRuntime _engine = null!;
    private NeedAnswers _answers = null!;
    private BackgroundWork _workQueue = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private Uri _base = null!;
    private readonly global::Assembly.Infrastructure.Topics.Topics _topics = new();

    [Theory]
    [InlineData("GET", "/version")]
    [InlineData("GET", "/machines")]
    [InlineData("GET", "/agents")]
    [InlineData("GET", "/sessions")]
    [InlineData("POST", "/sessions")]
    [InlineData("GET", "/sessions/retired-session")]
    [InlineData("GET", "/sessions/retired-session/rows")]
    [InlineData("GET", "/sessions/retired-session/tools/retired-item")]
    [InlineData("GET", "/sessions/retired-session/title")]
    [InlineData("GET", "/sessions/retired-session/title/content")]
    [InlineData("GET", "/sessions/retired-session/owner-prompts")]
    [InlineData("GET", "/sessions/retired-session/owner-prompts/client/content")]
    [InlineData("POST", "/sessions/retired-session/send")]
    [InlineData("POST", "/runs/retired-run/stop")]
    [InlineData("POST", "/sessions/retired-session/asks/retired-ask")]
    [InlineData("POST", "/sessions/retired-session/tasks/retired-task/stop")]
    [InlineData("POST", "/sessions/retired-session/attachments")]
    [InlineData("GET", "/sessions/retired-session/attachments/image.png")]
    [InlineData("GET", "/sessions/retired-session/config")]
    [InlineData("POST", "/sessions/retired-session/config")]
    [InlineData("POST", "/sessions/retired-session/config/warm")]
    [InlineData("POST", "/config/discover")]
    [InlineData("GET", "/live?session=retired-session&after=0")]
    public async Task Retired_HTTP_endpoints_return_unhandled_404_instead_of_legacy_error_or_success(string method, string path)
    {
        using var deadline = new CancellationTokenSource(Wait);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = new StringContent("{", Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, deadline.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // A legacy handler may also return 404 for an unknown id. Its JSON error must not masquerade as retirement.
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(deadline.Token));
        Assert.Equal(0, _launcher.Launches);
    }

    [Fact]
    public async Task Retired_live_query_token_does_not_authorize_anonymous_requests()
    {
        using var anonymous = new HttpClient { BaseAddress = _base };
        using var response = await anonymous.GetAsync($"/live?session=x&after=0&token={Token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var socket = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(
            SocketUri($"/live?session=x&after=0&token={Token}"), CancellationToken.None));
    }

    [Theory]
    [InlineData("/socket")]
    [InlineData("/socket/bulk")]
    [InlineData("/socket/terminal")]
    public async Task Retained_shared_host_sockets_authenticate_in_hello_and_reject_header_query_bypasses(string path)
    {
        using var deadline = new CancellationTokenSource(Wait);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(SocketUri(path), deadline.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes($$"""{"t":"hello","token":"{{Token}}","protocol":1}"""), WebSocketMessageType.Text, true, deadline.Token);
        var buffer = new byte[4096];
        var welcome = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
        Assert.Equal(WebSocketMessageType.Text, welcome.MessageType);
        using var frame = JsonDocument.Parse(buffer.AsMemory(0, welcome.Count));
        Assert.Equal("welcome", frame.RootElement.GetProperty("t").GetString());
        Assert.Equal(1, frame.RootElement.GetProperty("protocol").GetInt32());
        Assert.Equal(RowStore.FormatVersion, frame.RootElement.GetProperty("format").GetInt32());
        Assert.Equal(Environment.MachineName, frame.RootElement.GetProperty("machine").GetString());
        using var refused = new ClientWebSocket();
        refused.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
        await refused.ConnectAsync(SocketUri($"{path}?token={Token}"), deadline.Token);
        await refused.SendAsync(Encoding.UTF8.GetBytes("""{"t":"hello","token":"wrong","protocol":1}"""), WebSocketMessageType.Text, true, deadline.Token);
        var denied = await refused.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
        Assert.Equal(WebSocketMessageType.Close, denied.MessageType);
        Assert.Equal(4401, (int)refused.CloseStatus!);
    }

    public async Task InitializeAsync()
    {
        _database = AppDatabase.Open(_db.Path, _time, _topics);
        var store = RowStore.Open(_database);
        var summaries = SessionSummaries.Load(store);
        _sessionStore = SessionStore.Open(_database, _time, "checkouts", summaries);
        _hub = new LiveHub(store, topics: _topics, summaries: summaries, summaryChanged: _sessionStore.PublishSummary);
        var limits = new SessionLimits(() => 5, () => 10);
        _workQueue = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        _engine = EngineRuntime.Start(_database, _topics, TimeProvider.System, limits,
            cutover: new SessionCutover(_database, _sessionStore, _launcher, "checkouts", TimeProvider.System),
            effectsFactory: runtime =>
            {
                _sessions = new SessionRegistry(_hub, _sessionStore, _launcher, TimeProvider.System, TextWriter.Null, _workQueue,
                    feed: new EngineFeed(runtime, TextWriter.Null, TimeProvider.System));
                return new AdapterEffects(_database, _sessions, _sessionStore, store);
            });
        _answers = new NeedAnswers(new McpGate(_engine, limits, TimeProvider.System), _engine, _sessions, TimeProvider.System);
        var attachments = new AttachmentStore(Path.Combine(_work, "attachments"));
        var machines = new MachineSet(new LocalMachine(_hub, _sessionStore, _sessions, attachments));
        _machinesFeature = new MachinesFeature(machines, new MachineStatus(_topics, []), () => { }, TimeProvider.System);
        _app = ApiServer.Build(new ApiServices(_hub, _sessionStore, machines, attachments, Token,
            Writer: _database.Writer, Topics: _topics, Features: [new SessionsFeature(_sessionStore, machines),
                new SendFeature(_sessionStore, machines, _hub, new EngineDoor(_engine, _sessionStore, machines, attachments, TimeProvider.System, _answers)), new RowsFeature(_hub, store, _sessionStore, machines),
                new ConfigFeature(_sessionStore, machines), _machinesFeature,
                new AttachmentsFeature(_sessionStore, machines, Path.Combine(_work, "staging"))]), port: 0, out _);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _base = new Uri(address);
        _http = new HttpClient { BaseAddress = _base };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    [Fact]
    public void The_host_stops_within_its_shutdown_timeout()
    {
        Assert.Equal(ApiServer.ShutdownTimeout, _app.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    [Theory]
    [InlineData("tauri://localhost")]
    [InlineData("http://tauri.localhost")]
    [InlineData("http://localhost:1430")]
    [InlineData("http://localhost:1431")]
    [InlineData("http://localhost:1432")]
    [InlineData("http://localhost:1433")]
    [InlineData("http://localhost:1434")]
    [InlineData("http://localhost:1435")]
    [InlineData("http://localhost:1436")]
    [InlineData("http://localhost:1437")]
    [InlineData("http://localhost:1438")]
    [InlineData("http://localhost:1439")]
    [InlineData("https://example.com")]
    public async Task Browser_origins_get_no_cors_allowance_and_HTTP_auth_is_preserved(string origin)
    {
        using var anonymous = new HttpClient { BaseAddress = _base };
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/sessions/x/send");
        preflight.Headers.Add("Origin", origin);
        using var answer = await anonymous.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Equal("Authorization, Content-Type", answer.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.False(answer.Headers.Contains("Access-Control-Allow-Origin"));

        using var unauthorized = new HttpRequestMessage(HttpMethod.Get, "/sessions");
        unauthorized.Headers.Add("Origin", origin);
        using var denied = await anonymous.SendAsync(unauthorized);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/sessions?token={Token}")).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/sessions")).StatusCode);
        using var authorized = new HttpRequestMessage(HttpMethod.Get, "/sessions");
        authorized.Headers.Add("Origin", origin);
        using var listed = await _http.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.NotFound, listed.StatusCode);
        Assert.False(listed.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("no-store", listed.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/isolated-profile")]
    public async Task Agent_discovery_exposes_distinct_frozen_configuration_and_provider_targets_without_launch(string account)
    {
        var descriptor = new AgentDescriptor("configured-codex", "Configured Codex", "private-command-canary",
            ["private-argument-canary"], new Dictionary<string, string>
            { ["CODEX_HOME"] = account, ["PRIVATE_DISCOVERY_CANARY"] = "private-environment-canary" }, VendorFamily: "codex");
        _launcher.Agents = [descriptor];
        var capture = ConfigTargetIdentity.Capture(_launcher, descriptor.Id);
        var item = (await ResultAsync("machines", "agents", "{}")).GetProperty("agents").EnumerateArray().Single();
        Assert.Equal(["accountContext", "id", "name", "providerTargetId", "targetId"], item.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(descriptor.Id, item.GetProperty("id").GetString());
        Assert.Equal(descriptor.Name, item.GetProperty("name").GetString());
        Assert.Equal(capture.TargetId, item.GetProperty("targetId").GetString());
        var identity = account.Length == 0 ? "" : "acct-v1:bba8f0244af078a391d9faaebbd7df99750b21889fda2190393b2863a58c6e5b";
        Assert.True(identity == item.GetProperty("accountContext").GetString(), "Discovery account identity must be opaque.");
        Assert.True(descriptor.Id + "/" + identity == item.GetProperty("providerTargetId").GetString(), "Discovery provider target must contain only opaque account identity.");
        Assert.DoesNotContain("private-", item.GetRawText());
        Assert.Equal(0, _launcher.Launches);
        Assert.Empty(_sessionStore.List(null, 10).Sessions);

        _launcher.Agents = [descriptor with { Args = ["replacement-private-argument"] }];
        var replaced = (await ResultAsync("machines", "agents", "{}")).GetProperty("agents").EnumerateArray().Single();
        Assert.NotEqual(item.GetProperty("targetId").GetString(), replaced.GetProperty("targetId").GetString());
        Assert.Equal(item.GetProperty("providerTargetId").GetString(), replaced.GetProperty("providerTargetId").GetString());
        Assert.Equal(0, _launcher.Launches);
    }

    [Fact]
    public async Task Discovery_keeps_synthetic_home_private_while_retaining_exact_captured_launch_environment()
    {
        const string raw = "/synthetic/PRIVATE_ACCOUNT_CANARY_7b9f/配置";
        const string identity = "acct-v1:affb7550e6419024cafd75d2c3ed3d9f5c29f2dcc486ead8457a9efdac63a2c2";
        _launcher.Agents = [new("provider/with/slash", "Provider", "adapter", [],
            new Dictionary<string, string> { ["CODEX_HOME"] = raw }, VendorFamily: "codex")];
        var reply = await ResultAsync("machines", "agents", "{}");
        Assert.True(!reply.GetRawText().Contains("PRIVATE_ACCOUNT_CANARY_7b9f", StringComparison.Ordinal), "Private account material escaped discovery.");
        var target = reply.GetProperty("agents")[0];
        Assert.True(identity == target.GetProperty("accountContext").GetString());
        Assert.True("provider/with/slash/" + identity == target.GetProperty("providerTargetId").GetString());
        var captured = ConfigTargetIdentity.Capture(_launcher, "provider/with/slash");
        Assert.True(captured.Environment["CODEX_HOME"] == raw);
        Assert.Equal(captured.TargetId, target.GetProperty("targetId").GetString());
        Assert.Equal(0, _launcher.Launches);
        Assert.Empty(_sessionStore.List(null, 10).Sessions);
    }

    [Fact]
    public async Task A_session_runs_the_agent_it_was_made_with()
    {
        var agents = (await ResultAsync("machines", "agents", "{}")).GetProperty("agents").EnumerateArray()
            .Select(agent => (agent.GetProperty("id").GetString(), agent.GetProperty("name").GetString()));
        Assert.Equal([("claude", "Claude Code"), ("codex", "Codex")], agents);
        var created = await ResultAsync("sessions", "create", """{"agent":"codex","repoUrl":"https://github.com/o/r"}""");
        var sessionId = created.GetProperty("sessionId").GetString()!;
        Assert.Equal("codex", (await SessionAsync(sessionId)).GetProperty("agent").GetString());
        var clientId = await QueueAsync(sessionId);
        var agent = await _launcher.NextAsync();
        Assert.Equal(["codex"], _launcher.Launched);
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        using var rows = await OpenRowsAsync(_base, Token, sessionId, after: 0);
        RunOfPrompt(await ReadRowsAsync(rows, 2), sessionId, clientId);
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await ReadUntilTurnEndsAsync(rows);
    }

    [Fact]
    public async Task A_session_s_config_starts_from_its_agent_s_and_keeps_what_is_chosen()
    {
        var session = await CreateSessionAsync();
        var empty = await ResultAsync("config", "get", JsonSerializer.Serialize(new { session }));
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("options").ValueKind);
        _sessionStore.SetAgentConfig("claude", """[{"id":"model","type":"select","currentValue":"opus","options":[{"value":"opus"},{"value":"haiku"}]}]""");
        var config = await ResultAsync("config", "set", JsonSerializer.Serialize(new { session, configId = "model", value = "haiku" }));
        Assert.Equal("opus", config.GetProperty("options")[0].GetProperty("currentValue").GetString());
        Assert.Equal("haiku", config.GetProperty("choices").GetProperty("model").GetString());
        Assert.Equal("bad_args", (await RequestAsync("config", "set", JsonSerializer.Serialize(new { session, value = "x" }))).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Sessions_are_created_and_listed_newest_first()
    {
        var first = await CreateSessionAsync();
        _time.Advance(TimeSpan.FromMilliseconds(10));
        var second = await CreateSessionAsync();
        Assert.Equal("failed", (await RequestAsync("sessions", "create", """{"agent":"gemini","repoUrl":"x"}""")).GetProperty("code").GetString());
        var list = await ResultAsync("sessions", "page", "{}");
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
        _sessionStore.SetTitle(sessionId, "Fix the build", "user");
        var session = await SessionAsync(sessionId);
        Assert.Equal(sessionId, session.GetProperty("sessionId").GetString());
        Assert.Equal("Fix the build", session.GetProperty("title").GetString());
        Assert.Equal("user", session.GetProperty("titleSource").GetString());
        Assert.Empty((await TopicAsync("sessions", "nope")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_row_format_and_the_machines_are_reported()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(SocketUri("/socket"), CancellationToken.None);
        await SendTextAsync(socket, $$"""{"t":"hello","token":"{{Token}}","protocol":1}""");
        var welcome = await ReadFrameAsync(socket);
        Assert.Equal(RowStore.FormatVersion, welcome.GetProperty("format").GetInt32());
        Assert.Equal(Environment.MachineName, welcome.GetProperty("machine").GetString());
        var machines = (await TopicAsync("machines")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("local", Assert.Single(machines).GetProperty("key").GetString());
        Assert.Equal("failed", (await RequestAsync("sessions", "create", """{"agent":"claude","repoUrl":"r","machine":"nope"}""")).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_socket_reopened_mid_turn_carries_on_from_its_watermark()
    {
        var sessionId = await CreateSessionAsync();
        var clientId = await QueueAsync(sessionId);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "reopened-inserted");

        using (var first = await OpenRowsAsync(_base, Token, sessionId, after: 0))
        {
            var started = await ReadRowsAsync(first, 2);
            RunOfPrompt(started, sessionId, clientId);
            Assert.Equal([1L, 2L], started.Select(row => row.GetProperty("seq").GetInt64()));
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }

        await agent.TextAsync("Hello");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", prompt.GetProperty("params").GetProperty("sessionId").GetString()!);
        using var second = await OpenRowsAsync(_base, Token, sessionId, after: 2);
        AssertCaughtUpFrom(2, await ReadUntilTurnEndsAsync(second));
    }

    [Fact]
    public async Task An_opened_tool_row_gets_its_detail()
    {
        var sessionId = await CreateSessionAsync();
        var clientId = await QueueAsync(sessionId, "list files");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "tool-inserted");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"t1","title":"Terminal","kind":"execute","status":"pending","rawInput":{"command":"ls"}}""");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"t1","status":"completed","rawOutput":"a.txt"}""");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", prompt.GetProperty("params").GetProperty("sessionId").GetString()!);
        using var socket = await OpenRowsAsync(_base, Token, sessionId, after: 0);
        RunOfPrompt(await ReadRowsAsync(socket, 6), sessionId, clientId);
        var detail = await ResultAsync("rows", "tool", JsonSerializer.Serialize(new { session = sessionId, item = "t1" }), bulk: true);
        Assert.Equal("ls", detail.GetProperty("summary").GetString());
        Assert.Equal("""{"command":"ls"}""", detail.GetProperty("input").GetString());
        Assert.Equal("a.txt", detail.GetProperty("output").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("diff").ValueKind);
        Assert.Equal("failed", (await RequestAsync("rows", "tool", JsonSerializer.Serialize(new { session = sessionId, item = "nope" }), bulk: true)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stop_cancels_the_run_it_names()
    {
        var sessionId = await CreateSessionAsync();
        var clientId = await QueueAsync(sessionId);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "stopped-inserted");
        using var socket = await OpenRowsAsync(_base, Token, sessionId, after: 0);
        var started = await ReadRowsAsync(socket, 2);
        var runId = RunOfPrompt(started, sessionId, clientId);
        Assert.Equal("failed", (await RequestAsync("send", "stop", """{"runId":"nope"}""")).GetProperty("code").GetString());
        Assert.True((await ResultAsync("send", "stop", JsonSerializer.Serialize(new { runId }))).GetProperty("ok").GetBoolean());
        await agent.ReadAsync("session/cancel");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}""", prompt.GetProperty("params").GetProperty("sessionId").GetString()!);
        var rows = started.Concat(await ReadUntilTurnEndsAsync(socket)).ToList();
        Assert.Equal("cancelled", rows[^1].GetProperty("payload").GetProperty("state").GetString());
    }

    [Fact]
    public async Task An_image_isstored_by_path_and_read_back_by_name()
    {
        var session = await CreateSessionAsync();
        var saved = await UploadAsync(session, Png);
        Assert.Equal("result", saved.GetProperty("t").GetString());
        var path = saved.GetProperty("result").GetProperty("path").GetString()!;
        Assert.StartsWith(Path.Combine(_work, "attachments", session), path);
        Assert.Equal(Png, await File.ReadAllBytesAsync(path));
        var read = await ResultAsync("attachments", "read", JsonSerializer.Serialize(new { session, name = Path.GetFileName(path) }), bulk: true);
        Assert.Equal("image/png", read.GetProperty("mime").GetString());
        Assert.Equal(Png, read.GetProperty("data").GetBytesFromBase64());
    }

    [Fact]
    public async Task Only_real_images_arestored_and_only_their_own_names_are_served()
    {
        var session = await CreateSessionAsync();
        Assert.Equal("failed", (await UploadAsync(session, "not a png"u8.ToArray())).GetProperty("code").GetString());
        Assert.Equal("failed", (await RequestAsync("attachments", "begin", JsonSerializer.Serialize(new { session, mime = "text/plain", size = 5 }), true)).GetProperty("code").GetString());
        Assert.Equal("failed", (await RequestAsync("attachments", "begin", """{"session":"nope","mime":"image/png","size":11}""", true)).GetProperty("code").GetString());
        Assert.Equal("failed", (await RequestAsync("attachments", "read", JsonSerializer.Serialize(new { session, name = "../../assembly.db" }), true)).GetProperty("code").GetString());
        Assert.Equal("failed", (await RequestAsync("attachments", "read", JsonSerializer.Serialize(new { session, name = Guid.NewGuid() + ".png" }), true)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Composer_socket_discovery_returns_actual_metadata_without_a_session()
    {
        var targetId = _sessions.ConfigTargets[0].TargetId;
        var pending = ResultAsync("config", "discover", JsonSerializer.Serialize(new { agent = "claude", targetId, cwd = _work }));
        var agent = await _launcher.NextAsync();
        var opened = await agent.StartAsync(configOptions: "[]");
        Assert.Equal(_work, opened.GetProperty("params").GetProperty("cwd").GetString());
        var result = await pending.WaitAsync(Wait);
        Assert.Equal(JsonValueKind.Array, result.GetProperty("options").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("modelOptions").ValueKind);
        Assert.True(agent.Disposed);
        Assert.Empty(_sessionStore.List(null, 50, default).Sessions);
    }

    [Fact]
    public async Task Composer_socket_warm_resumes_the_saved_native_identity_without_a_prompt()
    {
        var session = _sessionStore.Create("claude", "repo", workingFolder: _work);
        _sessionStore.SetAcpSession(session.SessionId, "phone-restored");
        var targetId = _sessions.ConfigTargets[0].TargetId;
        var pending = ResultAsync("config", "warm", JsonSerializer.Serialize(new { session = session.SessionId, targetId }));
        var agent = await _launcher.NextAsync();
        var opened = await agent.StartAsync("phone-restored", configOptions: SessionWarmConfigTests.Options);
        Assert.Equal("session/resume", opened.GetProperty("method").GetString());
        Assert.Equal("phone-restored", opened.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(_work, opened.GetProperty("params").GetProperty("cwd").GetString());
        var result = await pending.WaitAsync(Wait);
        Assert.Equal("b", result.GetProperty("options")[0].GetProperty("currentValue").GetString());
        Assert.Equal("phone-restored", _sessionStore.Get(session.SessionId)!.AcpSessionId);
        Assert.True(agent.Disposed);
    }

    [Fact]
    public async Task Composer_socket_cancel_ends_its_exact_private_discovery_link()
    {
        using var socket = await OpenOwnerSocketAsync(_base, Token);
        var targetId = _sessions.ConfigTargets[0].TargetId;
        var pending = SocketRequestAsync(socket, 1, "config", "discover", JsonSerializer.Serialize(new { agent = "claude", targetId, cwd = _work }));
        var agent = await _launcher.NextAsync();
        await agent.ReadAsync("initialize");
        await SendTextAsync(socket, """{"t":"cancel","id":1}""");
        Assert.Equal("cancelled", (await pending).GetProperty("code").GetString());
        await agent.Exited.WaitAsync(Wait);
        Assert.True(agent.Disposed);
        Assert.Empty(_sessionStore.List(null, 50, default).Sessions);
    }
    private async Task<string> CreateSessionAsync() =>
        (await ResultAsync("sessions", "create", """{"agent":"claude","repoUrl":"https://github.com/o/r"}""")).GetProperty("sessionId").GetString()!;
    private async Task<JsonElement> RequestAsync(string feature, string method, string args, bool bulk = false)
    {
        using var socket = await OpenOwnerSocketAsync(_base, Token, bulk ? "/socket/bulk" : "/socket");
        return await SocketRequestAsync(socket, 1, feature, method, args);
    }
    private async Task<JsonElement> ResultAsync(string feature, string method, string args, bool bulk = false) =>
        Result(await RequestAsync(feature, method, args, bulk));
    internal static JsonElement Result(JsonElement frame)
    {
        Assert.Equal("result", frame.GetProperty("t").GetString());
        return frame.GetProperty("result").Clone();
    }
    private async Task<JsonElement> TopicAsync(string topic, string? key = null)
    {
        using var socket = await OpenOwnerSocketAsync(_base, Token);
        await SendTextAsync(socket, JsonSerializer.Serialize(new { t = "sub", id = 1, topic, key }));
        return await ReadFrameAsync(socket);
    }
    private async Task<JsonElement> SessionAsync(string session) =>
        Assert.Single((await TopicAsync("sessions", session)).GetProperty("items").EnumerateArray()).GetProperty("item").Clone();
    private async Task<JsonElement> UploadAsync(string session, byte[] data)
    {
        using var socket = await OpenOwnerSocketAsync(_base, Token, "/socket/bulk");
        var upload = Result(await SocketRequestAsync(socket, 1, "attachments", "begin", JsonSerializer.Serialize(new { session, mime = "image/png", size = data.Length }))).GetProperty("upload").GetString();
        Result(await SocketRequestAsync(socket, 2, "attachments", "chunk", JsonSerializer.Serialize(new { upload, offset = 0, data })));
        return await SocketRequestAsync(socket, 3, "attachments", "commit", JsonSerializer.Serialize(new { upload }));
    }
    private Uri SocketUri(string path) => new($"ws://{_base.Authority}{path}");
    internal static async Task<ClientWebSocket> OpenOwnerSocketAsync(Uri address, string token, string path = "/socket")
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{address.Authority}{path}"), CancellationToken.None).WaitAsync(Wait);
            await SendTextAsync(socket, JsonSerializer.Serialize(new { t = "hello", token, protocol = 1 }));
            Assert.Equal("welcome", (await ReadFrameAsync(socket)).GetProperty("t").GetString());
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
    internal static async Task<ClientWebSocket> OpenRowsAsync(Uri address, string token, string session, long after)
    {
        var socket = await OpenOwnerSocketAsync(address, token);
        try
        {
            await SendTextAsync(socket, JsonSerializer.Serialize(new { t = "sub", id = 1, topic = "rows", key = session, after }));
            var state = await ReadFrameAsync(socket);
            Assert.Equal("state", state.GetProperty("t").GetString());
            Assert.Equal(after, state.GetProperty("seq").GetInt64());
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
    internal static async Task<JsonElement> SocketRequestAsync(WebSocket socket, long id, string feature, string method, string args)
    {
        await SendTextAsync(socket, $$"""{"t":"call","id":{{id}},"feature":"{{feature}}","method":"{{method}}","args":{{args}}}""");
        var frame = await ReadFrameAsync(socket);
        Assert.Equal(id, frame.GetProperty("id").GetInt64());
        return frame;
    }
    private static Task SendTextAsync(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    internal static async Task<JsonElement> ReadFrameAsync(WebSocket socket)
    {
        using var deadline = new CancellationTokenSource(Wait);
        using var message = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            message.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        using var parsed = JsonDocument.Parse(message.ToArray());
        return parsed.RootElement.Clone();
    }
    /// <summary>A resumed sequence keeps its prevSeq chain and final message/stats/turn, with optional deltas.</summary>
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
    /// <summary>The next retained rows-topic frames; the subscription state has already been consumed.</summary>
    internal static async Task<List<JsonElement>> ReadRowsAsync(ClientWebSocket socket, int count)
    {
        var rows = new List<JsonElement>();
        while (rows.Count < count)
        {
            var frame = await ReadFrameAsync(socket);
            Assert.Equal("rows", frame.GetProperty("t").GetString());
            rows.Add(frame.GetProperty("row").Clone());
        }
        return rows;
    }
    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _machinesFeature.DisposeAsync();
        await _answers.DisposeAsync();
        await _engine.DisposeAsync();
        await _sessions.DisposeAsync();
        await _workQueue.DisposeAsync();
        _hub.Dispose();
        _database.Dispose();
        _db.Dispose();
        Directory.Delete(_work, recursive: true);
    }
}
