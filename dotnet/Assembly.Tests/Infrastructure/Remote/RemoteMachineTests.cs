using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Assembly.Infrastructure.Remote;
using Assembly.Mac.Serve;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Features.Chat.Sessions;

using Assembly.Tests.Features.Diagnostics;
namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>A Mac backend with a remote machine: a second backend on the same engine stands in for Workbox Test.</summary>
public sealed class RemoteMachineTests : IAsyncLifetime
{
    private const string MacToken = "mac-token";
    private const string BoxToken = "box-token";
    private const string Box = "workbox-test";

    private readonly string _macHome = Directory.CreateTempSubdirectory("assembly-mac-").FullName;
    private readonly string _boxHome = Directory.CreateTempSubdirectory("assembly-box-").FullName;
    private readonly FakeLauncher _macLauncher = new();
    private readonly FakeLauncher _boxLauncher = new();
    private readonly StringWriter _log = new();
    private MacBackend _box = null!;
    private MacBackend? _mac;

    public async Task InitializeAsync()
    {
        _box = await MacBackend.StartAsync(_boxHome, BoxToken, port: 0, _boxLauncher, _log);
        await StartMacAsync();
    }

    private async Task StartMacAsync() =>
        _mac = await MacBackend.StartAsync(_macHome, MacToken, port: 0, _macLauncher, _log, new RemoteConfig(Box, _box.Address, BoxToken));

    [Fact]
    public async Task A_thread_made_on_the_remote_runs_its_turn_there_and_its_rows_are_copied_here()
    {
        Assert.Equal([Box], (await GetJsonAsync(_mac!, MacToken, "/machines")).GetProperty("machines").EnumerateArray().Select(m => m.GetString()));
        var threadId = await CreateThreadAsync();
        Assert.Equal(Box, (await GetJsonAsync(_mac!, MacToken, $"/threads/{threadId}")).GetProperty("machine").GetString());
        Assert.Equal(JsonValueKind.Null, (await GetJsonAsync(_box, BoxToken, $"/threads/{threadId}")).GetProperty("machine").ValueKind);

        using var mac = await OpenLiveAsync(_mac!, MacToken, threadId, after: 0);
        var runId = await SendAsync(threadId);
        await RunTurnOnBoxAsync();

        var streamed = await ApiTests.ReadUntilTurnEndsAsync(mac);
        Assert.Equal(runId, streamed[^1].GetProperty("runId").GetString());

        // Stored the same on both, the finished message's deltas deleted on each.
        using var box = await OpenLiveAsync(_box, BoxToken, threadId, after: 0);
        using var reopened = await OpenLiveAsync(_mac!, MacToken, threadId, after: 0);
        Assert.Equal(Shape(await ApiTests.ReadUntilTurnEndsAsync(box)), Shape(await ApiTests.ReadUntilTurnEndsAsync(reopened)));
        Assert.Equal(0, _macLauncher.Launches);
    }

    [Fact]
    public async Task Opening_a_thread_catches_up_on_a_turn_that_ran_while_nobody_watched()
    {
        var threadId = await CreateThreadAsync();
        using var http = Client(_box, BoxToken);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/threads/{threadId}/send", Json("""{"text":"hi"}"""))).StatusCode);
        await RunTurnOnBoxAsync();
        using var box = await OpenLiveAsync(_box, BoxToken, threadId, after: 0);
        var ran = Shape(await ApiTests.ReadUntilTurnEndsAsync(box));

        await GetJsonAsync(_mac!, MacToken, $"/threads/{threadId}/rows");
        using var mac = await OpenLiveAsync(_mac!, MacToken, threadId, after: 0);

        Assert.Equal(ran, Shape(await ApiTests.ReadUntilTurnEndsAsync(mac)));
    }

    [Fact]
    public async Task A_mac_restarted_mid_turn_catches_up_instead_of_marking_the_turn_lost()
    {
        var threadId = await CreateThreadAsync();
        using (var watching = await OpenLiveAsync(_mac!, MacToken, threadId, after: 0))
        {
            await SendAsync(threadId);
            var agent = await _boxLauncher.NextAsync();
            await agent.StartAsync();
            var prompt = await agent.ReadAsync("session/prompt");
            await ApiTests.ReadRowsAsync(watching, 2);
            await _mac!.DisposeAsync();

            await agent.TextAsync("Hello");
            await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
            await agent.CloseAsync();
        }

        await StartMacAsync();
        using var mac = await OpenLiveAsync(_mac!, MacToken, threadId, after: 0);
        var rows = await ApiTests.ReadUntilTurnEndsAsync(mac);

        Assert.DoesNotContain(rows, row => row.GetProperty("kind").GetString() == "error");
        Assert.NotEqual("failed", rows[^1].GetProperty("payload").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_remote_that_writes_another_row_format_is_refused()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        await using var other = builder.Build();
        other.MapGet("/version", () => Results.Text("""{"formatVersion":99}""", "application/json"));
        await other.StartAsync();
        var address = new Uri(other.Urls.Single());
        await using var mac = await MacBackend.StartAsync(Directory.CreateTempSubdirectory("assembly-mac-").FullName, MacToken, 0, _macLauncher, _log,
            new RemoteConfig(Box, address, BoxToken));

        using var http = Client(mac, MacToken);
        var created = await http.PostAsync("/threads", Json($$"""{"agent":"claude","repoUrl":"https://github.com/o/r","machine":"{{Box}}"}"""));

        Assert.Equal(HttpStatusCode.BadGateway, created.StatusCode);
        Assert.Contains("format 99", await created.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_helper_of_another_agent_on_the_remote_keeps_its_agent_here()
    {
        var threadId = await CreateThreadAsync();
        using var mac = await OpenLiveAsync(_mac!, MacToken, threadId, after: 0);
        await SendAsync(threadId);
        var agent = await _boxLauncher.NextAsync();
        var open = await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        var token = open.GetProperty("params").GetProperty("mcpServers")[0].GetProperty("headers")[0].GetProperty("value").GetString()!;

        using var http = new HttpClient { BaseAddress = _box.Address };
        http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(token);
        var call = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"start_session","arguments":{"prompt":"Count the lines.","agent":"codex"}}}""";
        var answer = JsonDocument.Parse(await (await http.PostAsync("/mcp", Json(call))).Content.ReadAsStringAsync()).RootElement;
        var helperId = JsonDocument.Parse(answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!)
            .RootElement.GetProperty("threadId").GetString()!;

        while ((await ApiTests.ReadRowsAsync(mac, 1))[0].GetProperty("kind").GetString() != "subagent")
        {
        }
        var helper = await GetJsonAsync(_mac!, MacToken, $"/threads/{helperId}");
        Assert.Equal(("codex", threadId), (helper.GetProperty("agent").GetString(), helper.GetProperty("parentThreadId").GetString()));
    }

    [Fact]
    public async Task A_mac_session_starts_a_helper_on_the_remote_and_each_messages_the_other_once()
    {
        using var http = Client(_mac!, MacToken);
        var created = await http.PostAsync("/threads", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        var parent = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("threadId").GetString()!;
        await SendAsync(parent);
        var mac = await _macLauncher.NextAsync();
        var parentToken = SessionToken(await mac.StartAsync(acpSessionId: "acp-parent"));
        var parentPrompt = await mac.ReadAsync("session/prompt");

        var started = await ToolAsync(_mac!, parentToken, "start_session", new { prompt = "Count the lines.", agent = "codex", machine = Box });
        var helperId = started.GetProperty("threadId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        var helperToken = SessionToken(await box.StartAsync(acpSessionId: "acp-helper"));
        var helperPrompt = await box.ReadAsync("session/prompt");
        Assert.Contains("Count the lines.", helperPrompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal(parent, (await GetJsonAsync(_box, BoxToken, $"/threads/{helperId}")).GetProperty("parentThreadId").GetString());
        var copy = await GetJsonAsync(_mac!, MacToken, $"/threads/{helperId}");
        Assert.Equal((Box, "codex"), (copy.GetProperty("machine").GetString(), copy.GetProperty("agent").GetString()));

        // The parent's turn ends; its session stays open while its helper works on the box.
        await mac.ReplyAsync(parentPrompt, """{"stopReason":"end_turn"}""");

        // The helper asks its parent: queued on the box, picked up by the Mac, and delivered once.
        var asked = (await ToolAsync(_box, helperToken, "send_message", new { threadId = parent, text = "Which file?" })).GetProperty("messageId").GetString()!;
        var question = await mac.ReadAsync("session/prompt");
        Assert.Contains("Which file?", question.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        // As if the Mac stopped before telling the box: the box lists it again, and the Mac only tells it again.
        var boxDb = Path.Combine(_boxHome, "assembly.db");
        await Until(() => DeliveredRun(boxDb, asked) is not null);
        var run = DeliveredRun(boxDb, asked);
        ForgetDelivery(boxDb, asked);
        await Until(() => DeliveredRun(boxDb, asked) == run);
        await Assert.ThrowsAsync<TimeoutException>(() => mac.NextMessageAsync().WaitAsync(TimeSpan.FromSeconds(2)));

        // The parent's answer goes back to the box.
        await mac.TextAsync("notes.txt", "p2", "acp-parent");
        await mac.ReplyAsync(question, """{"stopReason":"end_turn"}""");
        var answered = await ToolAsync(_box, helperToken, "message_status", new { messageId = asked });
        Assert.Equal(("answered", "notes.txt"), (answered.GetProperty("state").GetString(), answered.GetProperty("reply").GetString()));

        // The helper answers; the parent reads it on the Mac, from the rows copied from the box.
        var status = ToolAsync(_mac!, parentToken, "message_status", new { messageId = started.GetProperty("messageId").GetString() });
        await box.TextAsync("notes.txt has 3 lines.", "h1", "acp-helper");
        await box.ReplyAsync(helperPrompt, """{"stopReason":"end_turn"}""");
        var reply = await status;
        Assert.Equal(("answered", "notes.txt has 3 lines."), (reply.GetProperty("state").GetString(), reply.GetProperty("reply").GetString()));
    }

    [Fact]
    public async Task A_message_left_queued_after_its_helper_ended_is_still_delivered_and_says_how_long_it_waited()
    {
        using var http = Client(_mac!, MacToken);
        var created = await http.PostAsync("/threads", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        var parent = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("threadId").GetString()!;
        await SendAsync(parent);
        var mac = await _macLauncher.NextAsync();
        var parentToken = SessionToken(await mac.StartAsync(acpSessionId: "acp-parent"));
        var parentPrompt = await mac.ReadAsync("session/prompt");
        var helperId = (await ToolAsync(_mac!, parentToken, "start_session", new { prompt = "Count the lines.", agent = "codex", machine = Box }))
            .GetProperty("threadId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        await box.StartAsync(acpSessionId: "acp-helper");
        await box.ReplyAsync(await box.ReadAsync("session/prompt"), """{"stopReason":"end_turn"}""");
        await mac.ReplyAsync(parentPrompt, """{"stopReason":"end_turn"}""");
        await Until(async () => (await GetJsonAsync(_mac!, MacToken, $"/threads/{parent}/rows")).GetProperty("rows").EnumerateArray()
            .Any(row => row.GetProperty("itemId").GetString() == $"subagent:{helperId}" && row.GetProperty("payload").GetProperty("state").GetString() == "idle"));
        await mac.CloseAsync();

        // As if the helper sent it and ended before the Mac looked: no helper of the parent's is at work on the box now.
        var boxDb = Path.Combine(_boxHome, "assembly.db");
        Queue(boxDb, "late-1", helperId, parent, "Which file?", DateTimeOffset.UtcNow.AddMinutes(-10));
        await mac.OpenAsync("acp-parent");
        var text = (await mac.ReadAsync("session/prompt")).GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString();
        Assert.Contains("Which file?", text);
        Assert.Contains("It was sent 10 min ago", text);
    }

    [Fact]
    public async Task Stop_on_a_remote_helper_s_row_stops_its_run_there_and_the_row_ends_stopped()
    {
        using var http = Client(_mac!, MacToken);
        var created = await http.PostAsync("/threads", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        var parent = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("threadId").GetString()!;
        await SendAsync(parent);
        var mac = await _macLauncher.NextAsync();
        var parentToken = SessionToken(await mac.StartAsync(acpSessionId: "acp-parent"));
        await mac.ReadAsync("session/prompt");
        var helperId = (await ToolAsync(_mac!, parentToken, "start_session", new { prompt = "Count the lines.", agent = "codex", machine = Box }))
            .GetProperty("threadId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        await box.StartAsync(acpSessionId: "acp-helper");
        var helperPrompt = await box.ReadAsync("session/prompt");

        // The row's Stop: refused until the helper's run has been copied here, then it reaches the box.
        await Until(async () => (await http.PostAsync($"/threads/{parent}/tasks/{helperId}/stop", null)).StatusCode == HttpStatusCode.OK);
        var cancel = await box.ReadAsync("session/cancel");
        Assert.Equal("acp-helper", cancel.GetProperty("params").GetProperty("sessionId").GetString());
        await box.ReplyAsync(helperPrompt, """{"stopReason":"cancelled"}""");

        await Until(async () =>
        {
            var rows = (await GetJsonAsync(_mac!, MacToken, $"/threads/{parent}/rows")).GetProperty("rows").EnumerateArray()
                .Where(row => row.GetProperty("itemId").GetString() == $"subagent:{helperId}").ToList();
            return rows.Count > 0 && rows[^1].GetProperty("payload") is var row
                && row.GetProperty("state").GetString() == "idle" && row.TryGetProperty("stopReason", out var reason) && reason.GetString() == "cancelled";
        });
    }

    private static string SessionToken(JsonElement open) =>
        open.GetProperty("params").GetProperty("mcpServers")[0].GetProperty("headers")[0].GetProperty("value").GetString()!["Bearer ".Length..];

    private static async Task<JsonElement> ToolAsync(MacBackend backend, string sessionToken, string tool, object arguments)
    {
        using var http = Client(backend, sessionToken);
        var call = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        var answer = JsonDocument.Parse(await (await http.PostAsync("/mcp", Json(call))).Content.ReadAsStringAsync()).RootElement.GetProperty("result");
        Assert.False(answer.GetProperty("isError").GetBoolean(), answer.GetRawText());
        return JsonDocument.Parse(answer.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    }

    private static string? DeliveredRun(string database, string messageId)
    {
        using var db = new SqliteConnection($"Data Source={database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT run_id FROM helper_messages WHERE message_id = $id;";
        command.Parameters.AddWithValue("$id", messageId);
        return command.ExecuteScalar() as string;
    }

    private static void Queue(string database, string messageId, string from, string to, string text, DateTimeOffset sent)
    {
        using var db = new SqliteConnection($"Data Source={database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO helper_messages(message_id, from_thread, to_thread, text, created_at) VALUES ($id, $from, $to, $text, $at);";
        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$at", sent.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static void ForgetDelivery(string database, string messageId)
    {
        using var db = new SqliteConnection($"Data Source={database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE helper_messages SET run_id = NULL WHERE message_id = $id;";
        command.Parameters.AddWithValue("$id", messageId);
        command.ExecuteNonQuery();
    }

    private static Task Until(Func<bool> condition) => Until(() => Task.FromResult(condition()));

    private static async Task Until(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
            await Task.Delay(50, deadline.Token);
    }

    private async Task RunTurnOnBoxAsync()
    {
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.TextAsync("Hello");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    private static List<(long, string?)> Shape(List<JsonElement> rows) =>
        rows.Select(row => (row.GetProperty("seq").GetInt64(), row.GetProperty("kind").GetString())).ToList();

    private async Task<string> CreateThreadAsync()
    {
        using var http = Client(_mac!, MacToken);
        var created = await http.PostAsync("/threads", Json($$"""{"agent":"claude","repoUrl":"https://github.com/o/r","machine":"{{Box}}"}"""));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("threadId").GetString()!;
    }

    private async Task<string> SendAsync(string threadId)
    {
        using var http = Client(_mac!, MacToken);
        var sent = await http.PostAsync($"/threads/{threadId}/send", Json("""{"text":"hi"}"""));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        return JsonDocument.Parse(await sent.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetString()!;
    }

    private static async Task<JsonElement> GetJsonAsync(MacBackend backend, string token, string path)
    {
        using var http = Client(backend, token);
        var answer = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static HttpClient Client(MacBackend backend, string token)
    {
        var http = new HttpClient { BaseAddress = backend.Address };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<ClientWebSocket> OpenLiveAsync(MacBackend backend, string token, string threadId, long after)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{backend.Address.Authority}/live?thread={threadId}&after={after}&token={token}"), CancellationToken.None);
        return socket;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public async Task DisposeAsync()
    {
        if (_mac is not null)
            await _mac.DisposeAsync();
        await _box.DisposeAsync();
        Directory.Delete(_macHome, recursive: true);
        Directory.Delete(_boxHome, recursive: true);
    }
}
