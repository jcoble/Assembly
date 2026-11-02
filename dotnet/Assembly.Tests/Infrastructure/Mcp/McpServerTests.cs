using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Mcp;
using Assembly.Engine.NeedsMe;
using Assembly.Infrastructure.Engine.Needs;
using Assembly.Application.Common;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Workflows.Contracts;
using Assembly.Application.Features.Workflows;
using Assembly.Infrastructure.Workflows;
using Assembly.Tests.Features.Chat.Sessions;
using ModelContextProtocol.Server;
using Assembly.Engine.Mcp.Tools;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Mcp;
using Assembly.Tests.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Mcp;

public sealed partial class McpServerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_http_gated_call_records_only_authenticated_token_claims(bool unauthenticatedIdentity)
    {
        await using var h = await Host.StartAsync(unauthenticatedIdentity);
        var result = await h.CallAsync("tools/call", """{"name":"list_sessions","arguments":{}}""");
        Assert.Equal(unauthenticatedIdentity, result.GetProperty("isError").GetBoolean());
        if (unauthenticatedIdentity)
        {
            Assert.Contains("session has ended", result.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal(0, h.Fixture.Count("engine_tool_call"));
        }
        else
        {
            Assert.Contains("Test", result.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("s|7|mcp|list_sessions|ok", h.Fixture.Text("SELECT session_id||'|'||epoch||'|'||boundary||'|'||tool||'|'||outcome FROM engine_tool_call"));
            Assert.NotNull(h.Fixture.Text("SELECT ended_ms FROM engine_tool_call"));
        }
    }

    [Fact]
    public async Task Stateless_sdk_lists_registered_tools_supports_old_protocol_and_rejects_non_session_bearers()
    {
        await using var h = await Host.StartAsync();
        var init = await h.CallAsync("initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"fixture","version":"1"}}""");
        Assert.Equal("assembly", init.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("2025-06-18", init.GetProperty("protocolVersion").GetString());
        var list = await h.CallAsync("tools/list", "{}");
        Assert.Equal(["list_sessions"], list.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, "/mcp");
            request.Headers.Authorization = new("Bearer", h.Token);
            using var response = await h.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
        foreach (var token in new string?[] { null, "unknown", "owner-api-token" }) await h.AssertUnauthorizedAsync(token);
        h.Tokens.Revoke("s");
        await h.AssertUnauthorizedAsync(h.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Board_adapter_preserves_existing_raw_results_and_definition_over_http(bool helper)
    {
        await using var h = await Host.StartAsync(board: true, helper: helper);
        var report = new BoardUpdateArgs("task", "Title \"界\"", "running", "Measured\nprogress", null, null, null, null, null);
        var args = JsonSerializer.SerializeToElement(report, WorkflowJson.Default.BoardUpdateArgs);
        var baseline = h.Board!.Call(h.SessionId, args);
        var result = await h.CallAsync("tools/call", "{\"name\":\"board_update\",\"arguments\":" + args.GetRawText() + "}");
        Assert.Equal(baseline.IsError, result.GetProperty("isError").GetBoolean());
        Assert.Equal(baseline.Text, Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal(helper ? "helper_forbidden" : "{", baseline.Text[..(helper ? baseline.Text.Length : 1)]);
        Assert.Equal(helper ? 0 : 1, h.BoardQueries!.Page(new(null, null, null), default).Items.Length);
        Assert.Equal(helper ? "refused" : "ok", h.Fixture.Text("SELECT outcome FROM engine_tool_call"));
        using var definition = new MemoryStream();
        using (var json = new Utf8JsonWriter(definition)) h.Board.WriteTool(json);
        using var expected = JsonDocument.Parse(definition.ToArray());
        var listed = (await h.CallAsync("tools/list", "{}")).GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "board_update");
        Assert.Equal(expected.RootElement.GetProperty("description").GetString(), listed.GetProperty("description").GetString());
        Assert.Equal(expected.RootElement.GetProperty("inputSchema").GetRawText(), listed.GetProperty("inputSchema").GetRawText());
        if (!helper)
        {
            var fresh = JsonSerializer.SerializeToElement(report with { TaskKey = "fresh" }, WorkflowJson.Default.BoardUpdateArgs);
            var created = await h.CallAsync("tools/call", "{\"name\":\"board_update\",\"arguments\":" + fresh.GetRawText() + "}");
            Assert.False(created.GetProperty("isError").GetBoolean());
            Assert.Equal(2, h.BoardQueries.Page(new(null, null, null), default).Items.Length);
        }
    }

    [Theory]
    [InlineData("{}", "bad_args")]
    [InlineData("{\"steps\":null}", "bad_args")]
    [InlineData("""{"taskKey":"t","title":"T","state":"bogus","summary":"S","taskUrl":null,"prUrl":null,"changedPaths":null,"question":null,"answerReceived":null,"steps":null}""", "invalid_argument")]
    public async Task Board_raw_errors_are_unquoted_and_recorded_as_refused(string args, string expected)
    {
        await using var h = await Host.StartAsync(board: true);
        var baseline = h.Board!.Call(h.SessionId, JsonDocument.Parse(args).RootElement);
        Assert.Equal((expected, true), baseline);
        var result = await h.CallAsync("tools/call", "{\"name\":\"board_update\",\"arguments\":" + args + "}");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(expected, Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        Assert.Empty(h.BoardQueries!.Page(new(null, null, null), default).Items);
        Assert.Equal("refused", h.Fixture.Text("SELECT outcome FROM engine_tool_call"));
    }

    [Fact]
    public async Task Start_recording_failure_is_refused_over_real_http()
    {
        await using var h = await Host.StartAsync(board: true);
        h.Fixture.Db.Writer.Write("test.recording-failure", scope => scope.Execute("""
            CREATE TRIGGER fail_mcp_start BEFORE INSERT ON engine_tool_call
            BEGIN SELECT RAISE(ABORT,'private storage failure'); END
            """));
        var args = JsonSerializer.SerializeToElement(new BoardUpdateArgs("task", "Title", "running", "Measured", null, null, null, null, null), WorkflowJson.Default.BoardUpdateArgs);
        var result = await h.CallAsync("tools/call", "{\"name\":\"board_update\",\"arguments\":" + args.GetRawText() + "}");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("could not record", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.DoesNotContain("private storage failure", result.GetRawText());
        Assert.Equal(0, h.Fixture.Count("engine_tool_call"));
        Assert.Empty(h.BoardQueries!.Page(new(null, null, null), default).Items);
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/")]
    public async Task Every_sdk_endpoint_spelling_requires_session_bearer(string path)
    {
        await using var h = await Host.StartAsync();
        await h.AssertUnauthorizedAsync(null, path);
    }

    [Fact]
    public async Task Current_official_client_initializes_lists_and_calls_through_real_http_transport()
    {
        await using var h = await Host.StartAsync();
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(h.Http.BaseAddress!, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + h.Token }
        }));
        Assert.Equal("assembly", client.ServerInfo!.Name);
        Assert.Equal(["list_sessions"], (await client.ListToolsAsync()).Select(tool => tool.Name));
        var result = await client.CallToolAsync("list_sessions");
        Assert.False(result.IsError);
        Assert.Contains("Test", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal("s|7|ok", h.Fixture.Text("SELECT session_id||'|'||epoch||'|'||outcome FROM engine_tool_call"));
    }

    [Fact]
    public async Task Bearer_continuation_receives_authenticated_validated_claims_only()
    {
        var tokens = new McpTokens { Url = "http://localhost/mcp" };
        var server = Assert.Single(tokens.ServersFor("s", 23));
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers.Authorization = server.GetProperty("headers")[0].GetProperty("value").GetString();
        context.User = new(new ClaimsIdentity([new("OwnerPeer", "spoof"), new("assembly:session", "spoof")], "spoof"));
        await new McpBearer(tokens).InvokeAsync(context, next =>
        {
            Assert.True(next.User.Identity!.IsAuthenticated);
            Assert.Equal("s", next.User.FindFirst("assembly:session")!.Value);
            Assert.Equal("23", next.User.FindFirst("assembly:epoch")!.Value);
            Assert.Equal(2, next.User.Claims.Count());
            return Task.CompletedTask;
        });
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly Topics _topics = new();
        public EngineFixture Fixture { get; }
        public McpTokens Tokens { get; } = new();
        public string Token { get; private set; } = null!;
        public string SessionId { get; private set; } = "s";
        public BoardTools? Board { get; private set; }
        public WorkflowQueries? BoardQueries { get; private set; }
        public NeedAnswers? Needs { get; private set; }
        public EngineRuntime Runtime => _runtime;
        private SessionRegistry? _sessions;
        private LiveHub? _hub;
        private BackgroundWork? _work;
        private McpEndpointReady _endpoint = null!;
        private EngineRuntime _runtime = null!;
        private WebApplication _app = null!;
        public HttpClient Http { get; private set; } = null!;
        private Host() => Fixture = new(topics: _topics);

        public static async Task<Host> StartAsync(bool unauthenticated = false, bool board = false, bool helper = false, McpServerTool? extraTool = null, Action<Microsoft.AspNetCore.Http.HttpContext>? requestFinished = null, bool needTools = false)
        {
            var h = new Host();
            if (board || needTools)
            {
                WorkflowSchema.Open(h.Fixture.Db);
                var store = SessionStore.Open(h.Fixture.Db, h.Fixture.Time, "checkouts");
                h._hub = new LiveHub(RowStore.Open(h.Fixture.Db));
                h._work = new(h.Fixture.Time, TextWriter.Null);
                h._sessions = new(h._hub, store, new FakeLauncher(), h.Fixture.Time, TextWriter.Null, h._work);
                if (board)
                {
                    h.BoardQueries = new(h.Fixture.Db);
                    h.Board = new(store, h._sessions, new(h.Fixture.Db, h.Fixture.Time), h.BoardQueries, default);
                    var session = store.Create("codex", "project");
                    h.SessionId = helper ? store.Helper(session, "codex", "Helper").SessionId : session.SessionId;
                }
            }
            h.Fixture.SeedSession(h.SessionId, epoch: 7);
            h.Fixture.SeedSession("child", parentId: h.SessionId);
            h._endpoint = new(h.Tokens);
            h._runtime = EngineRuntime.Start(h.Fixture.Db, h._topics, h.Fixture.Time, h.Fixture.Limits, effectsReady: h._endpoint.Ready);
            var gate = new McpGate(h._runtime, h.Fixture.Limits, h.Fixture.Time);
            IReadOnlyList<McpServerTool> tools = h.Board is null ? [new ListSessionsTool(gate)] : [new ListSessionsTool(gate), new BoardUpdateTool(gate, h.Board)];
            if (needTools)
            {
                h.Needs = new(gate, h._runtime, h._sessions!, h.Fixture.Time);
                tools = [.. tools, .. NeedTools.Create(gate, h.Needs)];
            }
            if (extraTool is not null) tools = [.. tools, extraTool];
            var host = new McpServerHost(h.Tokens, new(h._runtime, gate, h.Fixture.Time), tools);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            host.AddServices(builder.Services);
            h._app = builder.Build();
            host.Map(h._app);
            if (requestFinished is not null)
                h._app.Use(next => async context =>
                {
                    try { await next(context); }
                    finally { requestFinished(context); }
                });
            if (unauthenticated)
            {
                h._app.Use(next => context =>
                {
                    context.User = new(new ClaimsIdentity(context.User.Claims));
                    return next(context);
                });
            }
            await h._app.StartAsync();
            var address = h._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            h._endpoint.Listening(new Uri(address));
            var entry = Assert.Single(h._endpoint.ServersFor(h.SessionId, 7));
            h.Token = entry.GetProperty("headers")[0].GetProperty("value").GetString()![7..];
            h.Http = new() { BaseAddress = new Uri(address) };
            return h;
        }

        public async Task<JsonElement> CallAsync(string method, string args)
        {
            using var request = Request(Token, method, args);
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                body = body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("result").Clone();
        }

        public async Task AssertUnauthorizedAsync(string? token, string path = "/mcp")
        {
            using var request = Request(token, "tools/list", "{}");
            request.RequestUri = new Uri(path, UriKind.Relative);
            using var response = await Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        private static HttpRequestMessage Request(string? token, string method, string args)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent($$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{args}}}""", Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            var needWaits = Needs?.StopWaiting() ?? Task.CompletedTask;
            var stopping = _runtime.DisposeAsync().AsTask();
            _endpoint.Stop();
            await _app.DisposeAsync();
            await stopping;
            await needWaits;
            if (Needs is not null) await Needs.DisposeAsync();
            if (Board is not null) await Board.DisposeAsync();
            if (_sessions is not null) await _sessions.DisposeAsync();
            if (_work is not null) await _work.DisposeAsync();
            _hub?.Dispose();
            Fixture.Dispose();
        }
    }
}
