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
using Assembly.Desktop.Serve;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Features.Chat.Sessions;

using Assembly.Tests.Features.Diagnostics;
namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>A desktop backend with a remote machine: a second backend on the same engine stands in for Workbox Test.</summary>
public sealed class RemoteMachineTests : IAsyncLifetime
{
    private const string DesktopToken = "desktop-token";
    private const string BoxToken = "box-token";
    private const string Box = "workbox-test";

    private readonly string _desktopHome = Directory.CreateTempSubdirectory("assembly-desktop-").FullName;
    private readonly string _boxHome = Directory.CreateTempSubdirectory("assembly-box-").FullName;
    private readonly FakeLauncher _desktopLauncher = new();
    private readonly FakeLauncher _boxLauncher = new();
    private readonly StringWriter _log = new();
    private DesktopBackend _box = null!;
    private DesktopBackend? _desktop;

    public async Task InitializeAsync()
    {
        _box = await DesktopBackend.StartAsync(_boxHome, BoxToken, port: 0, _boxLauncher, _log);
        await StartDesktopAsync();
    }

    private async Task StartDesktopAsync() =>
        _desktop = await DesktopBackend.StartAsync(_desktopHome, DesktopToken, port: 0, _desktopLauncher, _log, new RemoteConfig(Box, _box.Address, BoxToken));

    [Fact]
    public async Task A_session_made_on_the_remote_runs_its_turn_there_and_its_rows_are_copied_here()
    {
        Assert.Equal([Box], (await GetJsonAsync(_desktop!, DesktopToken, "/machines")).GetProperty("machines").EnumerateArray().Select(m => m.GetString()));
        var sessionId = await CreateSessionAsync();
        Assert.Equal(Box, (await GetJsonAsync(_desktop!, DesktopToken, $"/sessions/{sessionId}")).GetProperty("machine").GetString());
        Assert.Equal(JsonValueKind.Null, (await GetJsonAsync(_box, BoxToken, $"/sessions/{sessionId}")).GetProperty("machine").ValueKind);

        using var desktop = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);
        var runId = await SendAsync(sessionId);
        await RunTurnOnBoxAsync();

        var streamed = await ApiTests.ReadUntilTurnEndsAsync(desktop);
        Assert.Equal(runId, streamed[^1].GetProperty("runId").GetString());

        // Stored the same on both, the finished message's deltas deleted on each.
        using var box = await OpenLiveAsync(_box, BoxToken, sessionId, after: 0);
        using var reopened = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);
        Assert.Equal(Shape(await ApiTests.ReadUntilTurnEndsAsync(box)), Shape(await ApiTests.ReadUntilTurnEndsAsync(reopened)));
        Assert.Equal(0, _desktopLauncher.Launches);
    }

    [Fact]
    public async Task Opening_a_session_catches_up_on_a_turn_that_ran_while_nobody_watched()
    {
        var sessionId = await CreateSessionAsync();
        using var http = Client(_box, BoxToken);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/sessions/{sessionId}/send", Json("""{"text":"hi"}"""))).StatusCode);
        await RunTurnOnBoxAsync();
        using var box = await OpenLiveAsync(_box, BoxToken, sessionId, after: 0);
        var ran = Shape(await ApiTests.ReadUntilTurnEndsAsync(box));

        await GetJsonAsync(_desktop!, DesktopToken, $"/sessions/{sessionId}/rows");
        using var desktop = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);

        Assert.Equal(ran, Shape(await ApiTests.ReadUntilTurnEndsAsync(desktop)));
    }

    // App Nap is held for a remote's turn too, from its copied rows, and by a Mac restarted while it still runs.
    [Fact]
    public async Task A_remote_turn_holds_app_nap_here_across_a_mac_restart_until_it_ends()
    {
        var sessionId = await CreateSessionAsync();
        await SendAsync(sessionId);
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        using (var watching = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0))
            await ApiTests.ReadRowsAsync(watching, 2);
        Assert.True(_desktop!.ActivityHeld);

        await _desktop.DisposeAsync();
        await StartDesktopAsync();
        Assert.True(_desktop.ActivityHeld);

        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        using var desktop = await OpenLiveAsync(_desktop, DesktopToken, sessionId, after: 0);
        await ApiTests.ReadUntilTurnEndsAsync(desktop);
        await Until(() => !_desktop.ActivityHeld);
    }

    [Fact]
    public async Task A_mac_restarted_mid_turn_catches_up_instead_of_marking_the_turn_lost()
    {
        var sessionId = await CreateSessionAsync();
        using (var watching = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0))
        {
            await SendAsync(sessionId);
            var agent = await _boxLauncher.NextAsync();
            await agent.StartAsync();
            var prompt = await agent.ReadAsync("session/prompt");
            await ApiTests.ReadRowsAsync(watching, 2);
            await _desktop!.DisposeAsync();

            await agent.TextAsync("Hello");
            await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
            await agent.CloseAsync();
        }

        await StartDesktopAsync();
        using var desktop = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);
        var rows = await ApiTests.ReadUntilTurnEndsAsync(desktop);

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
        await using var desktop = await DesktopBackend.StartAsync(Directory.CreateTempSubdirectory("assembly-desktop-").FullName, DesktopToken, 0, _desktopLauncher, _log,
            new RemoteConfig(Box, address, BoxToken));

        using var http = Client(desktop, DesktopToken);
        var created = await http.PostAsync("/sessions", Json($$"""{"agent":"claude","repoUrl":"https://github.com/o/r","machine":"{{Box}}"}"""));

        Assert.Equal(HttpStatusCode.BadGateway, created.StatusCode);
        Assert.Contains("format 99", await created.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_helper_of_another_agent_on_the_remote_keeps_its_agent_here()
    {
        var sessionId = await CreateSessionAsync();
        using var desktop = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);
        await SendAsync(sessionId);
        var agent = await _boxLauncher.NextAsync();
        var open = await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        var token = open.GetProperty("params").GetProperty("mcpServers")[0].GetProperty("headers")[0].GetProperty("value").GetString()!;

        using var http = new HttpClient { BaseAddress = _box.Address };
        http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(token);
        var call = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"start_session","arguments":{"prompt":"Count the lines.","agent":"codex"}}}""";
        var answer = JsonDocument.Parse(await (await http.PostAsync("/mcp", Json(call))).Content.ReadAsStringAsync()).RootElement;
        var helperId = JsonDocument.Parse(answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!)
            .RootElement.GetProperty("sessionId").GetString()!;

        while ((await ApiTests.ReadRowsAsync(desktop, 1))[0].GetProperty("kind").GetString() != "subagent")
        {
        }
        var helper = await GetJsonAsync(_desktop!, DesktopToken, $"/sessions/{helperId}");
        Assert.Equal(("codex", sessionId), (helper.GetProperty("agent").GetString(), helper.GetProperty("parentSessionId").GetString()));
    }

    [Fact]
    public async Task A_mac_session_starts_a_helper_on_the_remote_and_each_messages_the_other_once()
    {
        using var http = Client(_desktop!, DesktopToken);
        var created = await http.PostAsync("/sessions", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        var parent = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString()!;
        await SendAsync(parent);
        var desktop = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await desktop.StartAsync(acpSessionId: "acp-parent"));
        var parentPrompt = await desktop.ReadAsync("session/prompt");

        var started = await ToolAsync(_desktop!, parentToken, "start_session", new { prompt = "Count the lines.", agent = "codex", machine = Box });
        var helperId = started.GetProperty("sessionId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        var helperToken = SessionToken(await box.StartAsync(acpSessionId: "acp-helper"));
        var helperPrompt = await box.ReadAsync("session/prompt");
        Assert.Contains("Count the lines.", helperPrompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal(parent, (await GetJsonAsync(_box, BoxToken, $"/sessions/{helperId}")).GetProperty("parentSessionId").GetString());
        var copy = await GetJsonAsync(_desktop!, DesktopToken, $"/sessions/{helperId}");
        Assert.Equal((Box, "codex"), (copy.GetProperty("machine").GetString(), copy.GetProperty("agent").GetString()));

        // The parent's turn ends; its session stays open while its helper works on the box.
        await desktop.ReplyAsync(parentPrompt, """{"stopReason":"end_turn"}""");

        // The helper asks its parent: queued on the box, picked up by the desktop, and delivered once.
        var asked = (await ToolAsync(_box, helperToken, "send_message", new { sessionId = parent, text = "Which file?" })).GetProperty("messageId").GetString()!;
        var question = await desktop.ReadAsync("session/prompt");
        Assert.Contains("Which file?", question.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        // As if the desktop stopped before telling the box: the box lists it again, and the desktop only tells it again.
        var boxDb = Path.Combine(_boxHome, "assembly.db");
        await Until(() => DeliveredRun(boxDb, asked) is not null);
        var run = DeliveredRun(boxDb, asked);
        ForgetDelivery(boxDb, asked);
        await Until(() => DeliveredRun(boxDb, asked) == run);
        await Assert.ThrowsAsync<TimeoutException>(() => desktop.NextMessageAsync().WaitAsync(TimeSpan.FromSeconds(2)));

        // The parent's answer goes back to the box.
        await desktop.TextAsync("notes.txt", "p2", "acp-parent");
        await desktop.ReplyAsync(question, """{"stopReason":"end_turn"}""");
        var answered = await ToolAsync(_box, helperToken, "message_status", new { messageId = asked });
        Assert.Equal(("answered", "notes.txt"), (answered.GetProperty("state").GetString(), answered.GetProperty("reply").GetString()));

        // The helper answers; the parent reads it on the desktop, from the rows copied from the box.
        var status = ToolAsync(_desktop!, parentToken, "message_status", new { messageId = started.GetProperty("messageId").GetString() });
        await box.TextAsync("notes.txt has 3 lines.", "h1", "acp-helper");
        await box.ReplyAsync(helperPrompt, """{"stopReason":"end_turn"}""");
        var reply = await status;
        Assert.Equal(("answered", "notes.txt has 3 lines."), (reply.GetProperty("state").GetString(), reply.GetProperty("reply").GetString()));
    }

    [Fact]
    public async Task A_message_left_queued_after_its_helper_ended_is_still_delivered_and_says_how_long_it_waited()
    {
        using var http = Client(_desktop!, DesktopToken);
        var created = await http.PostAsync("/sessions", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        var parent = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString()!;
        await SendAsync(parent);
        var desktop = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await desktop.StartAsync(acpSessionId: "acp-parent"));
        var parentPrompt = await desktop.ReadAsync("session/prompt");
        var helperId = (await ToolAsync(_desktop!, parentToken, "start_session", new { prompt = "Count the lines.", agent = "codex", machine = Box }))
            .GetProperty("sessionId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        await box.StartAsync(acpSessionId: "acp-helper");
        await box.ReplyAsync(await box.ReadAsync("session/prompt"), """{"stopReason":"end_turn"}""");
        await desktop.ReplyAsync(parentPrompt, """{"stopReason":"end_turn"}""");
        await Until(async () => (await GetJsonAsync(_desktop!, DesktopToken, $"/sessions/{parent}/rows")).GetProperty("rows").EnumerateArray()
            .Any(row => row.GetProperty("itemId").GetString() == $"subagent:{helperId}" && row.GetProperty("payload").GetProperty("state").GetString() == "idle"));
        await desktop.CloseAsync();

        // As if the helper sent it and ended before the desktop looked: no helper of the parent's is at work on the box now.
        var boxDb = Path.Combine(_boxHome, "assembly.db");
        Queue(boxDb, "late-1", helperId, parent, "Which file?", DateTimeOffset.UtcNow.AddMinutes(-10));
        await desktop.OpenAsync("acp-parent");
        var text = (await desktop.ReadAsync("session/prompt")).GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString();
        Assert.Contains("Which file?", text);
        Assert.Contains("It was sent 10 min ago", text);
    }

    [Fact]
    public async Task Stop_on_a_remote_helper_s_row_stops_its_run_there_and_the_row_ends_stopped()
    {
        using var http = Client(_desktop!, DesktopToken);
        var created = await http.PostAsync("/sessions", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        var parent = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString()!;
        await SendAsync(parent);
        var desktop = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await desktop.StartAsync(acpSessionId: "acp-parent"));
        await desktop.ReadAsync("session/prompt");
        var helperId = (await ToolAsync(_desktop!, parentToken, "start_session", new { prompt = "Count the lines.", agent = "codex", machine = Box }))
            .GetProperty("sessionId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        await box.StartAsync(acpSessionId: "acp-helper");
        var helperPrompt = await box.ReadAsync("session/prompt");

        // The row's Stop: refused until the helper's run has been copied here, then it reaches the box.
        await Until(async () => (await http.PostAsync($"/sessions/{parent}/tasks/{helperId}/stop", null)).StatusCode == HttpStatusCode.OK);
        var cancel = await box.ReadAsync("session/cancel");
        Assert.Equal("acp-helper", cancel.GetProperty("params").GetProperty("sessionId").GetString());
        await box.ReplyAsync(helperPrompt, """{"stopReason":"cancelled"}""");

        await Until(async () =>
        {
            var rows = (await GetJsonAsync(_desktop!, DesktopToken, $"/sessions/{parent}/rows")).GetProperty("rows").EnumerateArray()
                .Where(row => row.GetProperty("itemId").GetString() == $"subagent:{helperId}").ToList();
            return rows.Count > 0 && rows[^1].GetProperty("payload") is var row
                && row.GetProperty("state").GetString() == "idle" && row.TryGetProperty("stopReason", out var reason) && reason.GetString() == "cancelled";
        });
    }

    private static string SessionToken(JsonElement open) =>
        open.GetProperty("params").GetProperty("mcpServers")[0].GetProperty("headers")[0].GetProperty("value").GetString()!["Bearer ".Length..];

    private static async Task<JsonElement> ToolAsync(DesktopBackend backend, string sessionToken, string tool, object arguments)
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
        command.CommandText = "INSERT INTO helper_messages(message_id, from_session, to_session, text, created_at) VALUES ($id, $from, $to, $text, $at);";
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

    private async Task<string> CreateSessionAsync()
    {
        using var http = Client(_desktop!, DesktopToken);
        var created = await http.PostAsync("/sessions", Json($$"""{"agent":"claude","repoUrl":"https://github.com/o/r","machine":"{{Box}}"}"""));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task<string> SendAsync(string sessionId)
    {
        using var http = Client(_desktop!, DesktopToken);
        var sent = await http.PostAsync($"/sessions/{sessionId}/send", Json("""{"text":"hi"}"""));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        return JsonDocument.Parse(await sent.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetString()!;
    }

    private static async Task<JsonElement> GetJsonAsync(DesktopBackend backend, string token, string path)
    {
        using var http = Client(backend, token);
        var answer = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static HttpClient Client(DesktopBackend backend, string token)
    {
        var http = new HttpClient { BaseAddress = backend.Address };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<ClientWebSocket> OpenLiveAsync(DesktopBackend backend, string token, string sessionId, long after)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{backend.Address.Authority}/live?session={sessionId}&after={after}&token={token}"), CancellationToken.None);
        return socket;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public async Task DisposeAsync()
    {
        if (_desktop is not null)
            await _desktop.DisposeAsync();
        await _box.DisposeAsync();
        Directory.Delete(_desktopHome, recursive: true);
        Directory.Delete(_boxHome, recursive: true);
    }
}
