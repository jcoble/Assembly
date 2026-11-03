using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Mcp;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Helpers;
using Assembly.Infrastructure.Machines;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Tests.Features.Chat.Sessions;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>The SDK on the real API, with the owning Engine worker and a scripted ACP agent.</summary>
public sealed class McpTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private readonly TempDatabase _db = new();
    private readonly string _work = Directory.CreateTempSubdirectory("assembly-mcp-").FullName;
    private readonly FakeLauncher _launcher = new();
    private readonly Topics _topics = new();
    private readonly McpTokens _tokens = new();
    private readonly Channel<EngineEvent> _events = Channel.CreateUnbounded<EngineEvent>();
    private AppDatabase _database = null!;
    private RowStore _store = null!;
    private LiveHub _hub = null!;
    private SessionStore _sessionStore = null!;
    private SessionRegistry _sessions = null!;
    private BackgroundWork _background = null!;
    private EngineRuntime _runtime = null!;
    private EngineHelperCards _cards = null!;
    private McpEndpointReady _endpoint = null!;
    private IDisposable _subscription = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _checkouts = null!;
    private int _ids;

    public async Task InitializeAsync()
    {
        _database = AppDatabase.Open(_db.Path, TimeProvider.System, _topics);
        _store = RowStore.Open(_database);
        _hub = new LiveHub(_store);
        _checkouts = Directory.CreateDirectory(Path.Combine(_work, "checkouts")).FullName;
        _sessionStore = SessionStore.Open(_database, TimeProvider.System, _checkouts);
        _background = new(TimeProvider.System, TextWriter.Null);
        _cards = new(_database, TimeProvider.System, TextWriter.Null, _sessionStore);
        _endpoint = new(_tokens);
        var limits = new SessionLimits(() => 1, () => 2);
        _runtime = EngineRuntime.Start(_database, _topics, TimeProvider.System, limits, messageLimit: () => 1,
            cutover: new(_database, _sessionStore, _launcher, _checkouts, TimeProvider.System, helperCards: _cards),
            effectsReady: _endpoint.Ready, effectsFactory: runtime =>
            {
                _sessions = new(_hub, _sessionStore, _launcher, TimeProvider.System, TextWriter.Null, _background,
                    feed: new EngineFeed(runtime, TextWriter.Null, TimeProvider.System, _cards),
                    mcp: McpSessionCallbacks.Create(runtime, _endpoint, _tokens));
                _sessions.BindHelperWorking(_cards.Working, _cards.Refresh);
                _cards.Start(_sessions.Helpers.Row);
                return new AdapterEffects(_database, _sessions, _sessionStore, _store);
            }, effectCompleted: (scope, id) =>
            {
                var child = scope.QueryFirst("SELECT session_id FROM engine_outbox WHERE id=$id", r => r.IsNull(0) ? null : r.Text(0), new SqlArg("$id", id));
                if (child is not null) _cards.Observe(scope, child);
            });
        _subscription = _runtime.Publisher.Register(e => _events.Writer.TryWrite(e), out _);
        var gate = new McpGate(_runtime, limits, TimeProvider.System);
        var sdk = new McpServerHost(_tokens, new(_runtime, gate, TimeProvider.System),
            [new StartSessionTool(gate, () => _launcher.Agents.Select(a => a.Id).ToArray(), Directory.Exists),
                new SendMessageTool(gate), new MessageStatusTool(gate), new ListSessionsTool(gate)]);
        var attachments = new AttachmentStore(Path.Combine(_work, "attachments"));
        var machines = new MachineSet(new LocalMachine(_hub, _sessionStore, _sessions, attachments));
        _app = ApiServer.Build(new(_hub, _sessionStore, machines, attachments, Token, sdk), 0, out _);
        await _app.StartAsync();
        var address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _endpoint.Listening(address);
        _http = new() { BaseAddress = address };
    }

    [Fact]
    public async Task A_session_starts_a_helper_reads_its_answer_and_stays_open_while_it_works()
    {
        var parent = CreateParent();
        QueueOwner(parent, "delegate it");
        var agent = await _launcher.NextAsync();
        var parentToken = SessionToken(await agent.StartAsync(acpSessionId: "acp-parent", canSteer: false));
        var parentPrompt = await agent.ReadAsync("session/prompt");
        var parentDone = _sessions.For(parent.SessionId).CurrentTurn.Done;

        using var unauthorized = await McpAsync(Token, "tools/list", "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var initialized = await ResultAsync(parentToken, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}""");
        Assert.Equal("assembly", initialized.GetProperty("serverInfo").GetProperty("name").GetString());
        var tools = (await ResultAsync(parentToken, "tools/list", "{}")).GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToArray();
        foreach (var required in new[] { "start_session", "send_message", "message_status", "list_sessions" }) Assert.Contains(required, tools);

        var started = await CallAsync(parentToken, "start_session", new { prompt = "Count the lines in notes.txt.", title = "Count lines", provider = "codex" });
        var helperId = started.GetProperty("sessionId").GetString()!;
        var messageId = started.GetProperty("messageId").GetString()!;
        Assert.True(Guid.TryParse(helperId, out _));
        Assert.True(Guid.TryParse(messageId, out _));
        Assert.Equal("queued", (await CallAsync(parentToken, "message_status", new { messageId })).GetProperty("state").GetString());
        var helperAgent = await _launcher.NextAsync();
        var helperToken = SessionToken(await helperAgent.StartAsync(acpSessionId: "acp-helper"));
        Assert.NotEqual(parentToken, helperToken);
        var foreignStatus = await CallResultAsync(helperToken, "message_status", new { messageId });
        Assert.True(foreignStatus.GetProperty("isError").GetBoolean());
        Assert.Equal("No message of yours has that id.", foreignStatus.GetProperty("content")[0].GetProperty("text").GetString());
        var helperPrompt = await helperAgent.ReadAsync("session/prompt");
        Assert.Contains("Count the lines in notes.txt.", helperPrompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await helperAgent.InsertAsync(helperPrompt, "helper-inserted");
        await MessageStateAsync(messageId, "next_turn");
        Assert.Equal("next_turn", (await CallAsync(parentToken, "message_status", new { messageId })).GetProperty("state").GetString());
        Assert.Equal(parent.SessionId, _store.RowsAfter(helperId, 0).Last(row => row.Kind == "user").Payload.GetProperty("from").GetString());
        var helper = _sessionStore.Get(helperId)!;
        Assert.Equal((parent.SessionId, true, "codex"), (helper.ParentSessionId, helper.Helper, helper.Agent));
        Assert.True((await CallResultAsync(helperToken, "start_session", new { prompt = "more", title = "Too deep" })).GetProperty("isError").GetBoolean());

        await agent.FinishAsync(parentPrompt, """{"stopReason":"end_turn"}""");
        await parentDone.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_sessions.IsOpen(parent.SessionId));
        Assert.True(_sessions.StartedWorking(parent.SessionId));
        QueueOwner(parent, "Wait for the helper's result.");
        var followup = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(followup, "parent-followup-inserted");
        await helperAgent.TextAsync("notes.txt has 3 lines.", "h1", "acp-helper");
        await helperAgent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", "acp-helper");
        Assert.Equal("acp-helper", await helperAgent.CloseAsync());
        await ReplyPhaseAsync(helperId, parent.SessionId, inFlight: 0);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", "acp-parent");
        var reply = await agent.ReadAsync("session/close");
        {
            Assert.Equal("acp-parent", reply.GetProperty("params").GetProperty("sessionId").GetString());
            await ReplyPhaseAsync(helperId, parent.SessionId, inFlight: 1);
            await agent.ReplyAsync(reply, "{}");
            var oldToken = parentToken;
            JsonElement resumed;
            try { resumed = await agent.OpenAsync("acp-parent"); }
            catch (TimeoutException error)
            {
                var messages = _database.Query("SELECT id,state,in_flight,reason FROM engine_message WHERE to_session_id=$id",
                    row => $"{row.Text(0)}:{row.Text(1)}:in_flight={row.Long(2)}:{(row.IsNull(3) ? "" : row.Text(3))}", default, new SqlArg("$id", parent.SessionId));
                var effects = _database.Query("SELECT id,state,last_error FROM engine_outbox WHERE session_id=$id",
                    row => $"{row.Text(0)}:{row.Text(1)}:{(row.IsNull(2) ? "" : row.Text(2))}", default, new SqlArg("$id", parent.SessionId));
                var session = _database.QueryFirst("SELECT state,activity_state,epoch FROM engine_session WHERE id=$id",
                    row => $"{row.Text(0)}:activity={row.Text(1)}:epoch={row.Long(2)}", default, new SqlArg("$id", parent.SessionId));
                throw new TimeoutException($"C6 parent resume: session=[{session}]; messages=[{string.Join(';', messages)}]; effects=[{string.Join(';', effects)}]", error);
            }
            Assert.Equal("session/resume", resumed.GetProperty("method").GetString());
            parentToken = SessionToken(resumed);
            Assert.NotEqual(oldToken, parentToken);
            using var revoked = await McpAsync(oldToken, "tools/list", "{}");
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            reply = await agent.ReadAsync("session/prompt");
        }
        Assert.Equal("session/prompt", reply.GetProperty("method").GetString());
        Assert.Contains("notes.txt has 3 lines.", reply.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal(helperId, _store.RowsAfter(parent.SessionId, 0).Last(row => row.Kind == "user").Payload.GetProperty("from").GetString());
        Assert.Equal("next_turn", (await CallAsync(parentToken, "message_status", new { messageId })).GetProperty("state").GetString());
        await agent.FinishAsync(reply, """{"stopReason":"end_turn"}""");
        Assert.Equal("acp-parent", await agent.CloseAsync());

        using var revokedParent = await McpAsync(parentToken, "tools/list", "{}");
        using var revokedHelper = await McpAsync(helperToken, "tools/list", "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, revokedParent.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedHelper.StatusCode);
        await _cards.DisposeAsync();
        var rows = _store.RowsAfter(parent.SessionId, 0).Where(row => row.ItemId == $"subagent:{helperId}").ToList();
        Assert.Equal(["running", "idle"], rows.Select(row => row.Payload.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task Helpers_have_limits_at_work_and_between_any_two_sessions()
    {
        var parent = CreateParent();
        QueueOwner(parent, "delegate twice");
        var agent = await _launcher.NextAsync();
        var parentToken = SessionToken(await agent.StartAsync(acpSessionId: "acp-parent"));
        await agent.ReadAsync("session/prompt");
        var firstReply = await CallAsync(parentToken, "start_session", new { prompt = "one", title = "One" });
        var first = firstReply.GetProperty("sessionId").GetString()!;
        var firstToken = SessionToken(await agent.OpenAsync("acp-one"));
        var firstPrompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(firstPrompt, "first-inserted");
        var secondReply = await CallAsync(parentToken, "start_session", new { prompt = "two", title = "Two" });
        var second = secondReply.GetProperty("sessionId").GetString()!;
        await agent.OpenAsync("acp-two");
        var secondPrompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(secondPrompt, "second-inserted");
        Assert.True((await CallResultAsync(parentToken, "start_session", new { prompt = "three", title = "Three" })).GetProperty("isError").GetBoolean());

        var listed = (await CallAsync(firstToken, "list_sessions", new { })).GetProperty("sessions").EnumerateArray()
            .Select(entry => (entry.GetProperty("sessionId").GetString(), entry.GetProperty("relation").GetString()));
        Assert.Contains((parent.SessionId, "starter"), listed);
        Assert.Contains((second, "sibling"), listed);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", "acp-two");
        Assert.Equal("acp-two", await agent.CloseAsync());
        var sent = await CallAsync(firstToken, "send_message", new { sessionId = second, text = "What did you find?" });
        Assert.Equal("queued", sent.GetProperty("state").GetString());
        await agent.OpenAsync("acp-two");
        var delivered = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(delivered, "sibling-inserted");
        await MessageStateAsync(sent.GetProperty("messageId").GetString()!, "next_turn");
        Assert.Contains("What did you find?", delivered.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal(first, _store.RowsAfter(second, 0).Last(row => row.Kind == "user").Payload.GetProperty("from").GetString());
        Assert.True((await CallResultAsync(firstToken, "send_message", new { sessionId = second, text = "again" })).GetProperty("isError").GetBoolean());
        Assert.True((await CallResultAsync(parentToken, "send_message", new { sessionId = second, text = "and you?" })).GetProperty("isError").GetBoolean());
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", "acp-two");
        await agent.CloseAsync();
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", "acp-one");
        await agent.CloseAsync();
    }

    private SessionInfo CreateParent()
    {
        var parent = _sessionStore.Create("claude", "https://github.com/o/r");
        Directory.CreateDirectory(Path.Combine(_checkouts, parent.SessionId));
        return parent;
    }

    private void QueueOwner(SessionInfo parent, string text)
    {
        var client = Guid.NewGuid().ToString("N");
        var prepared = OwnerPromptFingerprint.Prepare(_runtime.Machine, new SendOwnerPrompt(parent.SessionId, text, client, [], null), default);
        var id = OwnerPromptIdentity.MessageId(parent.SessionId, client);
        var context = new CommandContext(OwnerPromptIdentity.CommandId(_runtime.Machine, parent.SessionId, client),
            _runtime.Machine, new(ActorKind.Window, "owner"), id, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.True(((IEngineRuntime)_runtime).Write(scope => _runtime.Commands.ApplyOwnerPrompt(scope, context, prepared)).Applied);
    }

    private async Task MessageStateAsync(string id, string state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (_database.QueryFirst("SELECT state FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", id)) != state)
            await _events.Reader.ReadAsync(timeout.Token);
    }

    private async Task ReplyPhaseAsync(string helper, string parent, long inFlight)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string state = "missing";
        try
        {
            while (true)
            {
                var reply = _database.QueryFirst("""
                    SELECT id,state,in_flight,reason FROM engine_message
                    WHERE from_session_id=$helper AND to_session_id=$parent AND id LIKE 'turn-reply/%'
                    """, row => new { Id = row.Text(0), State = row.Text(1), InFlight = row.Long(2), Reason = row.IsNull(3) ? null : row.Text(3) },
                    default, new SqlArg("$helper", helper), new("$parent", parent));
                if (reply is not null)
                {
                    state = $"{reply.Id}:{reply.State}:in_flight={reply.InFlight}:{reply.Reason}";
                    Assert.True(reply.State == "queued", $"C6 delivery before parent close ACK: {state}");
                    if (reply.InFlight == inFlight)
                    {
                        Console.WriteLine($"C6 protocol phase: {state}");
                        return;
                    }
                }
                await _events.Reader.ReadAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"C6 delivery before parent close ACK: {state}", error);
        }
    }

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
        using var answer = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        return answer.RootElement.Clone();
    }

    private Task<JsonElement> CallResultAsync(string token, string tool, object arguments) =>
        ResultAsync(token, "tools/call", JsonSerializer.Serialize(new { name = tool, arguments }));

    private async Task<JsonElement> ResultAsync(string token, string method, string parameters)
    {
        using var response = await McpAsync(token, method, parameters);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("result").Clone();
    }

    private Task<HttpResponseMessage> McpAsync(string token, string method, string parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent($$"""{"jsonrpc":"2.0","id":{{Interlocked.Increment(ref _ids)}},"method":"{{method}}","params":{{parameters}}}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using (request) return await _http.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        var stopping = _runtime.DisposeAsync().AsTask();
        _endpoint.Stop();
        await _app.DisposeAsync();
        await _sessions.DisposeAsync();
        await stopping;
        await _cards.DisposeAsync();
        _subscription.Dispose();
        await _background.DisposeAsync();
        _hub.Dispose(); _database.Dispose(); _db.Dispose();
        Directory.Delete(_work, recursive: true);
    }
}
