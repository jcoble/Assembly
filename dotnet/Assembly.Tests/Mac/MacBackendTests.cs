using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Mac.Serve;
using Assembly.Mac.Window;
using Assembly.Tests.Api;
using Assembly.Tests.Sessions;

namespace Assembly.Tests.Mac;

/// <summary>Assembly.Mac's backend on a free port, with a scripted agent behind it.</summary>
public sealed class MacBackendTests : IAsyncLifetime
{
    private const string Token = "mac-token";

    private readonly string _home = Directory.CreateTempSubdirectory("assembly-mac-").FullName;
    private readonly FakeLauncher _launcher = new();
    private readonly StringWriter _log = new();
    private MacBackend _backend = null!;
    private bool _disposed;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _backend = await MacBackend.StartAsync(_home, Token, port: 0, _launcher, _log);
        _http = new HttpClient { BaseAddress = _backend.Address };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    [Fact]
    public async Task A_local_thread_runs_a_turn_through_the_mac_backend()
    {
        var threadId = await CreateThreadAsync();
        var runId = await SendAsync(threadId);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.TextAsync("Hello");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");

        using var socket = await OpenLiveAsync(threadId, after: 0);
        var rows = await ApiTests.ReadUntilTurnEndsAsync(socket);
        Assert.Equal(["user", "turn"], rows.Take(2).Select(row => row.GetProperty("kind").GetString()));
        ApiTests.AssertCaughtUpFrom(2, rows[2..]);
        Assert.Equal(runId, rows[^1].GetProperty("runId").GetString());
        Assert.Equal(JsonValueKind.Null, (await GetJsonAsync($"/threads/{threadId}")).GetProperty("machine").ValueKind);
        Assert.True(File.Exists(Path.Combine(_home, "assembly.db")));
        await agent.CloseAsync();
        await Until(() => _log.ToString().Contains($"thread {threadId} run {runId}: initialize "));
    }

    [Fact]
    public async Task Debug_drop_closes_the_webviews_sockets_and_a_reopened_one_gets_what_it_missed()
    {
        var threadId = await CreateThreadAsync();
        await SendAsync(threadId);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        using var first = await OpenLiveAsync(threadId, after: 0);
        Assert.Equal([1L, 2L], (await ApiTests.ReadRowsAsync(first, 2)).Select(row => row.GetProperty("seq").GetInt64()));

        using var anonymous = new HttpClient { BaseAddress = _backend.Address };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/debug/drop", Json("""{"ms":5000}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.PostAsync("/debug/drop", Json("""{"ms":5000}"""))).StatusCode);
        await ClosedAsync(first);

        await agent.TextAsync("Hello");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        using var second = await OpenLiveAsync(threadId, after: 2);
        ApiTests.AssertCaughtUpFrom(2, await ApiTests.ReadUntilTurnEndsAsync(second));
    }

    [Fact]
    public async Task Stopping_closes_the_webviews_sockets_first_so_it_stops_quickly()
    {
        var threadId = await CreateThreadAsync();
        using var socket = await OpenLiveAsync(threadId, after: 0);

        var clock = Stopwatch.StartNew();
        await DisposeBackendAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"stopping took {clock.Elapsed.TotalSeconds:0.0} s");
        await ClosedAsync(socket);
    }

    [Fact]
    public async Task The_window_uploads_a_chosen_image_to_the_backend_until_it_is_discarded()
    {
        var threadId = await CreateThreadAsync();
        var image = Path.Combine(_home, "shot.png");
        await File.WriteAllBytesAsync(image, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        var bridge = new WindowBridge(Token, Path.Combine(_home, "soak"), _backend.Address, () => []);
        bridge.Chosen.Add([image]);
        var upload = JsonSerializer.Serialize(new { id = 1, method = "attachment.upload_path", @params = new { threadId, path = image } });

        var uploaded = JsonDocument.Parse((await bridge.AnswerAsync(upload))!).RootElement;
        await bridge.AnswerAsync(JsonSerializer.Serialize(new { id = 2, method = "attachment.discard", @params = new { path = image } }));
        var again = JsonDocument.Parse((await bridge.AnswerAsync(upload))!).RootElement;

        Assert.False(string.IsNullOrEmpty(uploaded.GetProperty("result").GetProperty("path").GetString()), uploaded.ToString());
        Assert.Equal("Only images you picked or dropped can be attached", again.GetProperty("error").GetString());
    }

    private async Task<string> CreateThreadAsync()
    {
        var created = await _http.PostAsync("/threads", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("threadId").GetString()!;
    }

    private async Task<string> SendAsync(string threadId)
    {
        var sent = await _http.PostAsync($"/threads/{threadId}/send", Json("""{"text":"hi"}"""));
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

    private async Task<ClientWebSocket> OpenLiveAsync(string threadId, long after)
    {
        var socket = new ClientWebSocket();
        var uri = new Uri($"ws://{_backend.Address.Authority}/live?thread={threadId}&after={after}&token={Token}");
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
