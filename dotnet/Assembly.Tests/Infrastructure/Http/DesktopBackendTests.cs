using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Assembly.Desktop.Serve;
using Assembly.Infrastructure.Diagnostics;
using Assembly.Infrastructure.Http;
using Assembly.Tests.Features.Chat.Sessions;

using Assembly.Tests.Features.Diagnostics;
using Assembly.Tests.Infrastructure.Remote;
using Assembly.Tests.Infrastructure.Socket;
namespace Assembly.Tests.Infrastructure.Http;

/// <summary>Assembly.Desktop's backend on a free port, with a scripted agent behind it.</summary>
public sealed class DesktopBackendTests : IAsyncLifetime
{
    private const string Token = "desktop-token";

    private readonly string _home = Directory.CreateTempSubdirectory("assembly-desktop-").FullName;
    private readonly FakeLauncher _launcher = new();
    private readonly SharedLog _log = new();
    private DesktopBackend _backend = null!;
    private bool _disposed;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _backend = await DesktopBackend.StartAsync(_home, Token, port: 0, _launcher, _log);
        _http = new HttpClient { BaseAddress = _backend.Address };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    [Fact]
    public async Task Serve_refuses_a_remote_named_local_before_anything_starts()
    {
        var home = Path.Combine(_home, "refused");
        var tokenFile = Path.Combine(home, "env");
        var errors = new StringWriter();
        var stderr = Console.Error;
        Console.SetError(errors);
        try
        {
            Assert.Equal(2, await DesktopServe.MainAsync(["--remote", "local", "--home", home, "--token-file", tokenFile]));
        }
        finally
        {
            Console.SetError(stderr);
        }

        Assert.Contains("a remote can't be named local, the key for this computer", errors.ToString());
        Assert.False(File.Exists(tokenFile));
    }

    [Fact]
    public async Task A_local_session_runs_a_turn_through_the_mac_backend()
    {
        var sessionId = await CreateSessionAsync();
        var runId = await SendAsync(sessionId);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.TextAsync("Hello");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");

        using var socket = await OpenLiveAsync(sessionId, after: 0);
        var rows = await ApiTests.ReadUntilTurnEndsAsync(socket);
        Assert.Equal(["user", "turn"], rows.Take(2).Select(row => row.GetProperty("kind").GetString()));
        ApiTests.AssertCaughtUpFrom(2, rows[2..]);
        Assert.Equal(runId, rows[^1].GetProperty("runId").GetString());
        Assert.Equal(JsonValueKind.Null, (await GetJsonAsync($"/sessions/{sessionId}")).GetProperty("machine").ValueKind);
        Assert.True(File.Exists(Path.Combine(_home, "assembly.db")));
        await agent.CloseAsync();
        await Until(() => _log.ToString().Contains($"session {sessionId} run {runId}: initialize "));
    }

    // App Nap is held while a turn runs, by the backend from its rows, with no window calls.
    [Fact]
    public async Task A_running_turn_holds_app_nap_until_it_ends()
    {
        var sessionId = await CreateSessionAsync();
        Assert.False(_backend.ActivityHeld);
        await SendAsync(sessionId);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.True(_backend.ActivityHeld);

        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");

        await Until(() => !_backend.ActivityHeld);
        await agent.CloseAsync();
    }

    [Fact]
    public async Task The_socket_serves_the_desktops_preferences()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{_backend.Address.Authority}/socket"), CancellationToken.None);
        await SendTextAsync(socket, $$"""{"t":"hello","token":"{{Token}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadTextAsync(socket));

        await SendTextAsync(socket, """{"t":"call","id":1,"feature":"preferences","method":"set","args":{"key":"theme","value":"dark"}}""");

        Assert.Equal("""{"t":"result","id":1,"result":null}""", await ReadTextAsync(socket));
    }

    [Fact]
    public async Task The_socket_serves_the_desktops_clock()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{_backend.Address.Authority}/socket"), CancellationToken.None);
        await SendTextAsync(socket, $$"""{"t":"hello","token":"{{Token}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadTextAsync(socket));

        await SendTextAsync(socket, """{"t":"sub","id":1,"topic":"clock","key":"second"}""");

        Assert.StartsWith("""{"t":"state","sub":1,"items":[{"key":"second","item":{"now":""", await ReadTextAsync(socket));
    }

    [Fact]
    public async Task The_socket_serves_the_desktops_rows()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{_backend.Address.Authority}/socket"), CancellationToken.None);
        await SendTextAsync(socket, $$"""{"t":"hello","token":"{{Token}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadTextAsync(socket));

        await SendTextAsync(socket, """{"t":"sub","id":1,"topic":"rows","key":"no-such-session"}""");

        Assert.Equal("""{"t":"state","sub":1,"seq":0,"items":[]}""", await ReadTextAsync(socket));
    }

    [Fact]
    // The desktop's drop ends every open socket, the caller's too, so the window's reconnect can be tried.
    public async Task The_desktops_drop_ends_every_open_socket()
    {
        using var other = await OpenSocketAsync();
        using var socket = await OpenSocketAsync();

        await SendTextAsync(socket, """{"t":"call","id":1,"feature":"diagnostics","method":"drop","args":{}}""");

        await SocketTests.AssertEndsAsync(other);
        await SocketTests.AssertEndsAsync(socket);
    }

    private async Task<ClientWebSocket> OpenSocketAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{_backend.Address.Authority}/socket"), CancellationToken.None);
        await SendTextAsync(socket, $$"""{"t":"hello","token":"{{Token}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadTextAsync(socket));
        return socket;
    }

    [Fact]
    public async Task The_socket_serves_the_desktops_sessions()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{_backend.Address.Authority}/socket"), CancellationToken.None);
        await SendTextAsync(socket, $$"""{"t":"hello","token":"{{Token}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadTextAsync(socket));

        await SendTextAsync(socket, """{"t":"sub","id":1,"topic":"sessions","key":"no-such-session"}""");

        Assert.Equal("""{"t":"state","sub":1,"items":[]}""", await ReadTextAsync(socket));
    }

    [Fact]
    public async Task The_socket_takes_the_desktops_sends()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{_backend.Address.Authority}/socket"), CancellationToken.None);
        await SendTextAsync(socket, $$"""{"t":"hello","token":"{{Token}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadTextAsync(socket));

        await SendTextAsync(socket, """{"t":"call","id":1,"feature":"send","method":"stop","args":{"runId":"no-such-run"}}""");

        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"that run isn't running"}""", await ReadTextAsync(socket));
    }

    [Fact]
    public async Task The_socket_serves_the_desktops_config_and_machines()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{_backend.Address.Authority}/socket"), CancellationToken.None);
        await SendTextAsync(socket, $$"""{"t":"hello","token":"{{Token}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadTextAsync(socket));

        await SendTextAsync(socket, """{"t":"call","id":1,"feature":"config","method":"get","args":{"session":"no-such-session"}}""");
        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"unknown session"}""", await ReadTextAsync(socket));
        await SendTextAsync(socket, """{"t":"call","id":2,"feature":"machines","method":"agents","args":{"machine":"no-such-machine"}}""");
        Assert.Equal("""{"t":"error","id":2,"code":"failed","message":"unknown machine"}""", await ReadTextAsync(socket));
    }

    [Fact]
    public async Task The_socket_takes_the_desktops_attachments()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{_backend.Address.Authority}/socket"), CancellationToken.None);
        await SendTextAsync(socket, $$"""{"t":"hello","token":"{{Token}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadTextAsync(socket));

        await SendTextAsync(socket, """{"t":"call","id":1,"feature":"attachments","method":"begin","args":{"session":"no-such-session","mime":"image/png","size":10}}""");

        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"unknown session"}""", await ReadTextAsync(socket));
    }

    private static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReadTextAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    [Fact]
    public async Task Version_names_the_commit_built_and_a_session_names_its_agent_session()
    {
        var sessionId = await CreateSessionAsync();
        await SendAsync(sessionId);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");

        Assert.Matches("^[0-9a-f]{40}$", (await GetJsonAsync("/version")).GetProperty("commit").GetString());
        await Until(() => GetJsonAsync($"/sessions/{sessionId}").Result.GetProperty("acpSessionId").ValueKind == JsonValueKind.String);
        await agent.CloseAsync();
    }

    [Fact]
    public async Task Debug_drop_closes_the_webviews_sockets_and_a_reopened_one_gets_what_it_missed()
    {
        var sessionId = await CreateSessionAsync();
        await SendAsync(sessionId);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        using var first = await OpenLiveAsync(sessionId, after: 0);
        Assert.Equal([1L, 2L], (await ApiTests.ReadRowsAsync(first, 2)).Select(row => row.GetProperty("seq").GetInt64()));

        using var anonymous = new HttpClient { BaseAddress = _backend.Address };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/debug/drop", Json("""{"ms":5000}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.PostAsync("/debug/drop", Json("""{"ms":5000}"""))).StatusCode);
        await ClosedAsync(first);

        await agent.TextAsync("Hello");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        using var second = await OpenLiveAsync(sessionId, after: 2);
        ApiTests.AssertCaughtUpFrom(2, await ApiTests.ReadUntilTurnEndsAsync(second));
    }

    [Fact]
    public async Task Stopping_closes_the_webviews_sockets_first_so_it_stops_quickly()
    {
        var sessionId = await CreateSessionAsync();
        using var socket = await OpenLiveAsync(sessionId, after: 0);

        var clock = Stopwatch.StartNew();
        await DisposeBackendAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"stopping took {clock.Elapsed.TotalSeconds:0.0} s");
        await ClosedAsync(socket);
    }

    [Fact]
    public async Task The_page_uploads_an_image_by_its_path_and_appends_soak_lines_and_an_unknown_call_is_404()
    {
        var sessionId = await CreateSessionAsync();
        var image = Path.Combine(_home, "shot.png");
        await File.WriteAllBytesAsync(image, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);

        var uploaded = await _http.PostAsync("/page/attachment.upload_path", Json(JsonSerializer.Serialize(new { sessionId, path = image })));
        var soak = await _http.PostAsync("/page/soak.append", Json("""{"name":"run.csv","line":"a,b"}"""));
        var notAnImage = await _http.PostAsync("/page/attachment.upload_path", Json(JsonSerializer.Serialize(new { sessionId, path = _home })));
        var unknown = await _http.PostAsync("/page/token.get", null);

        var result = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync()).RootElement;
        Assert.False(string.IsNullOrEmpty(result.GetProperty("result").GetProperty("path").GetString()), result.ToString());
        Assert.Equal(HttpStatusCode.OK, soak.StatusCode);
        Assert.Equal("a,b\n", await File.ReadAllTextAsync(Path.Combine(_home, "soak-results", "run.csv")));
        Assert.Equal(HttpStatusCode.BadRequest, notAnImage.StatusCode);
        Assert.Equal("Only files can be attached", JsonDocument.Parse(await notAnImage.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_second_backend_on_the_same_home_fails_and_writes_nothing()
    {
        var sessionId = await CreateSessionAsync();
        await SendAsync(sessionId);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        var before = Rows();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopBackend.StartAsync(_home, Token, port: 0, _launcher, _log));

        Assert.StartsWith("home in use by pid ", error.Message);
        Assert.Equal(before, Rows());
        Assert.Contains(before, row => row.StartsWith($"{sessionId}:", StringComparison.Ordinal) && row.Contains(":turn:", StringComparison.Ordinal));
        await GetJsonAsync("/version");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_backend_that_fails_to_start_releases_its_home()
    {
        var home = Directory.CreateTempSubdirectory("assembly-desktop-").FullName;
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => DesktopBackend.StartAsync(home, Token, _backend.Address.Port, _launcher, _log));

            await using var again = await DesktopBackend.StartAsync(home, Token, port: 0, _launcher, _log);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>Every row in the home's database, read apart from the backend.</summary>
    private List<string> Rows()
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(_home, "assembly.db")};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT session_id || ':' || seq || ':' || kind || ':' || payload FROM rows ORDER BY session_id, seq;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        return rows;
    }

    private async Task<string> CreateSessionAsync()
    {
        var created = await _http.PostAsync("/sessions", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task<string> SendAsync(string sessionId)
    {
        var sent = await _http.PostAsync($"/sessions/{sessionId}/send", Json("""{"text":"hi"}"""));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        return JsonDocument.Parse(await sent.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetString()!;
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var answer = await _http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<ClientWebSocket> OpenLiveAsync(string sessionId, long after)
    {
        var socket = new ClientWebSocket();
        var uri = new Uri($"ws://{_backend.Address.Authority}/live?session={sessionId}&after={after}&token={Token}");
        await socket.ConnectAsync(uri, CancellationToken.None);
        return socket;
    }

    /// <summary>Reads until the server closes the socket.</summary>
    private static async Task ClosedAsync(ClientWebSocket socket)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[64 * 1024];
        try
        {
            while ((await socket.ReceiveAsync(buffer, deadline.Token)).MessageType != WebSocketMessageType.Close)
            {
            }
        }
        catch (WebSocketException)
        {
        }

        Assert.NotEqual(WebSocketState.Open, socket.State);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    private async Task DisposeBackendAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _backend.DisposeAsync();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await DisposeBackendAsync();
        Directory.Delete(_home, recursive: true);
    }
}
