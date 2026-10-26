using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Mcp;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Features.Chat.Helpers;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>The Assembly MCP server on the real API, with a scripted agent behind it: helper sessions and messages.</summary>
public sealed class McpTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private readonly TempDatabase _db = new();
    private readonly string _work = Directory.CreateTempSubdirectory("assembly-mcp-").FullName;
    private readonly FakeLauncher _launcher = new();
    private AppDatabase _database = null!;
    private RowStore _store = null!;
    private LiveHub _hub = null!;
    private ThreadStore _threads = null!;
    private SessionRegistry _sessions = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private Uri _base = null!;
    private int _ids;

    public async Task InitializeAsync()
    {
        _database = AppDatabase.Open(_db.Path);
        _store = RowStore.Open(_database);
        _hub = new LiveHub(_store);
        _threads = ThreadStore.Open(_database, TimeProvider.System);
        _sessions = new SessionRegistry(_hub, _threads, _launcher, TimeProvider.System, TextWriter.Null, new HelperLimits(UnfinishedHelpers: 2, MessagesPerPair: 1));
        var attachments = new AttachmentStore(Path.Combine(_work, "attachments"));
        var machines = new MachineSet(new LocalMachine(_hub, _threads, _sessions, attachments));
        _sessions.Helpers.Delivery.Machines = machines;
        _app = ApiServer.Build(new ApiServices(_hub, _threads, machines, attachments, Token, _sessions.Helpers), port: 0);
        await _app.StartAsync();
        _base = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _sessions.Helpers.Tokens.Url = new Uri(_base, "/mcp").ToString();
        _http = new HttpClient { BaseAddress = _base };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    [Fact]
    public async Task A_session_starts_a_helper_reads_its_answer_and_stays_open_while_it_works()
    {
        var parent = _threads.Create("claude", "https://github.com/o/r");
        _sessions.For(parent.ThreadId).Send("delegate it");
        var agent = await _launcher.NextAsync();
        var parentToken = SessionToken(await agent.StartAsync(acpSessionId: "acp-parent"));
        var parentPrompt = await agent.ReadAsync("session/prompt");

        // Only a session's own token opens the server; the API's doesn't.
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(Token, "tools/list", "{}")).StatusCode);
        var initialized = await ResultAsync(parentToken, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}""");
        Assert.Equal("assembly", initialized.GetProperty("serverInfo").GetProperty("name").GetString());
        var tools = (await ResultAsync(parentToken, "tools/list", "{}")).GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString());
        Assert.Equal(["start_session", "send_message", "message_status", "list_sessions"], tools);

        var started = await CallAsync(parentToken, "start_session", new { prompt = "Count the lines in notes.txt.", agent = "codex" });
        var helperId = started.GetProperty("threadId").GetString()!;
        var messageId = started.GetProperty("messageId").GetString()!;
        var helper = _threads.Get(helperId)!;
        Assert.Equal((parent.ThreadId, true, "codex"), (helper.ParentThreadId, helper.Helper, helper.Agent));

        // The helper is a session of its own, on its agent's adapter, with a token of its own.
        var helperAgent = await _launcher.NextAsync();
        var helperToken = SessionToken(await helperAgent.StartAsync(acpSessionId: "acp-helper"));
        Assert.NotEqual(parentToken, helperToken);
        var helperPrompt = await helperAgent.ReadAsync("session/prompt");
        Assert.Contains("Count the lines in notes.txt.", helperPrompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        var refused = await CallResultAsync(helperToken, "start_session", new { prompt = "more" });
        Assert.True(refused.GetProperty("isError").GetBoolean());

        // The parent's turn ends while its helper works: its session stays open, and message_status waits for the answer.
        await agent.ReplyAsync(parentPrompt, """{"stopReason":"end_turn"}""");
        var status = CallAsync(parentToken, "message_status", new { messageId });
        await Task.Delay(500);
        Assert.False(status.IsCompleted);
        await helperAgent.TextAsync("notes.txt has 3 lines.", "h1", "acp-helper");
        await helperAgent.ReplyAsync(helperPrompt, """{"stopReason":"end_turn"}""");
        var answered = await status;
        Assert.Equal(("answered", "notes.txt has 3 lines."), (answered.GetProperty("state").GetString(), answered.GetProperty("reply").GetString()));

        Assert.Equal("acp-helper", await helperAgent.CloseAsync());
        Assert.Equal("acp-parent", await agent.CloseAsync());

        // A closed session's token stops working.
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(parentToken, "tools/list", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(helperToken, "tools/list", "{}")).StatusCode);
        var rows = _store.RowsAfter(parent.ThreadId, 0).Where(row => row.ItemId == $"subagent:{helperId}").ToList();
        Assert.Equal(["running", "idle"], rows.Select(row => row.Payload.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task Helpers_have_limits_at_work_and_between_any_two_sessions()
    {
        var parent = _threads.Create("claude", "https://github.com/o/r");
        _sessions.For(parent.ThreadId).Send("delegate twice");
        var agent = await _launcher.NextAsync();
        var parentToken = SessionToken(await agent.StartAsync(acpSessionId: "acp-parent"));
        await agent.ReadAsync("session/prompt");

        var first = (await CallAsync(parentToken, "start_session", new { prompt = "one" })).GetProperty("threadId").GetString()!;
        var firstToken = SessionToken(await agent.OpenAsync("acp-one"));
        var firstPrompt = await agent.ReadAsync("session/prompt");
        var second = (await CallAsync(parentToken, "start_session", new { prompt = "two" })).GetProperty("threadId").GetString()!;
        await agent.OpenAsync("acp-two");
        var secondPrompt = await agent.ReadAsync("session/prompt");
        Assert.True((await CallResultAsync(parentToken, "start_session", new { prompt = "three" })).GetProperty("isError").GetBoolean());

        // A helper reaches its parent and its sibling.
        var listed = (await CallAsync(firstToken, "list_sessions", new { })).GetProperty("sessions").EnumerateArray()
            .Select(entry => (entry.GetProperty("threadId").GetString(), entry.GetProperty("relation").GetString()));
        Assert.Contains((parent.ThreadId, "parent"), listed);
        Assert.Contains((second, "sibling"), listed);

        // The second finishes; the first may message it once.
        await agent.ReplyAsync(secondPrompt, """{"stopReason":"end_turn"}""");
        Assert.Equal("acp-two", await agent.CloseAsync());
        var sent = await CallAsync(firstToken, "send_message", new { threadId = second, text = "What did you find?" });
        Assert.True(sent.TryGetProperty("messageId", out _));
        await agent.OpenAsync("acp-two");
        var delivered = await agent.ReadAsync("session/prompt");
        Assert.Contains("What did you find?", delivered.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        // Its row in the receiving thread says who sent it, so the page doesn't show it as the person's.
        Assert.Equal(first, _store.RowsAfter(second, 0).Last(row => row.Kind == "user").Payload.GetProperty("from").GetString());
        Assert.True((await CallResultAsync(firstToken, "send_message", new { threadId = second, text = "again" })).GetProperty("isError").GetBoolean());
        // The limit holds between any two sessions: the parent's prompt was its one message to the helper.
        Assert.True((await CallResultAsync(parentToken, "send_message", new { threadId = second, text = "and you?" })).GetProperty("isError").GetBoolean());
        await agent.ReplyAsync(delivered, """{"stopReason":"end_turn"}""");
        await agent.ReplyAsync(firstPrompt, """{"stopReason":"end_turn"}""");
    }

    // The session's token for the Assembly MCP server, from its session/new or session/resume: kept in memory only.
    private static string SessionToken(JsonElement open)
    {
        var server = Assert.Single(open.GetProperty("params").GetProperty("mcpServers").EnumerateArray());
        Assert.Equal(("http", "assembly"), (server.GetProperty("type").GetString(), server.GetProperty("name").GetString()));
        var header = Assert.Single(server.GetProperty("headers").EnumerateArray());
        Assert.Equal("Authorization", header.GetProperty("name").GetString());
        return header.GetProperty("value").GetString()!["Bearer ".Length..];
    }

    private async Task<JsonElement> CallAsync(string token, string tool, object arguments)
    {
        var result = await CallResultAsync(token, tool, arguments);
        Assert.False(result.GetProperty("isError").GetBoolean(), result.GetRawText());
        return JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;
    }

    private Task<JsonElement> CallResultAsync(string token, string tool, object arguments) =>
        ResultAsync(token, "tools/call", JsonSerializer.Serialize(new { name = tool, arguments }));

    private async Task<JsonElement> ResultAsync(string token, string method, string parameters)
    {
        var answer = await McpAsync(token, method, parameters);
        answer.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement.GetProperty("result");
    }

    private async Task<HttpResponseMessage> McpAsync(string token, string method, string parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent($$"""{"jsonrpc":"2.0","id":{{++_ids}},"method":"{{method}}","params":{{parameters}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var anonymous = new HttpClient { BaseAddress = _base };
        return await anonymous.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        await _sessions.DisposeAsync();
        _hub.Dispose();
        _database.Dispose();
        _db.Dispose();
        Directory.Delete(_work, recursive: true);
    }
}
