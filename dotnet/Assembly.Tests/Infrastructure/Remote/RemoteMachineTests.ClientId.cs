using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Desktop.Serve;
using Assembly.Tests.Infrastructure.Http;

namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>A send retried with the same clientId: across a restart, and through the desktop to the remote.</summary>
public sealed partial class RemoteMachineTests
{
    private const string BadClientId = "clientId must be 1 to 64 letters, digits, - or _";

    [Fact]
    public async Task A_send_retried_after_a_restart_answers_its_first_run_and_starts_no_turn()
    {
        using var http = Client(_desktop!, DesktopToken);
        var created = await http.PostAsync("/sessions", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        var sessionId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString()!;
        string runId;
        using (var socket = await OpenSocketAsync(_desktop!))
            runId = RunOf(await CallSendAsync(socket, 1, sessionId, "hi", "c1"));
        var agent = await _desktopLauncher.NextAsync();
        await agent.StartAsync();
        await agent.ReplyAsync(await agent.ReadAsync("session/prompt"), """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();

        await _desktop!.DisposeAsync();
        await StartDesktopAsync();
        using var again = await OpenSocketAsync(_desktop);
        Assert.Equal($$$"""{"t":"result","id":1,"result":{"runId":"{{{runId}}}"}}""", await CallSendAsync(again, 1, sessionId, "hi", "c1"));

        // The next message is the first prompt the restarted agent sees: the retry started no turn.
        await CallSendAsync(again, 2, sessionId, "two", "c2");
        var restarted = await _desktopLauncher.NextAsync();
        await restarted.StartAsync();
        var prompt = await restarted.ReadAsync("session/prompt");
        Assert.Equal("two", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await restarted.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await restarted.CloseAsync();
        Assert.Single(await RowsAsync(_desktop, DesktopToken, sessionId), row => ClientIdOf(row) == "c1");
    }

    [Fact]
    public async Task A_send_retried_through_the_desktop_answers_its_first_run_with_one_prompt_on_the_remote()
    {
        var sessionId = await CreateSessionAsync();
        using var socket = await OpenSocketAsync(_desktop!);
        var runId = RunOf(await CallSendAsync(socket, 1, sessionId, "hi", "c5"));
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");

        Assert.Equal($$$"""{"t":"result","id":2,"result":{"runId":"{{{runId}}}"}}""", await CallSendAsync(socket, 2, sessionId, "hi", "c5"));

        // The next thing the remote's agent hears is the turn's close: no second prompt or steer.
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        using var desktop = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);
        var user = Assert.Single(await ApiTests.ReadUntilTurnEndsAsync(desktop), row => row.GetProperty("kind").GetString() == "user");
        Assert.Equal("c5", ClientIdOf(user));
    }

    [Fact]
    public async Task A_resend_through_the_desktop_with_other_text_fails_with_the_remotes_words()
    {
        var sessionId = await CreateSessionAsync();
        using var socket = await OpenSocketAsync(_desktop!);
        RunOf(await CallSendAsync(socket, 1, sessionId, "hi", "c16"));
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");

        var failed = await CallSendAsync(socket, 2, sessionId, "bye", "c16");

        Assert.StartsWith("""{"t":"error","id":2,"code":"failed",""", failed);
        Assert.Contains("clientId already used for another message", failed);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_send_whose_desktop_request_drops_still_lands_on_the_remote_and_its_retry_answers_that_run()
    {
        var sessionId = await CreateSessionAsync();
        using var socket = await OpenSocketAsync(_desktop!);
        var runId = RunOf(await CallSendAsync(socket, 1, sessionId, "hi", "c17"));
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");

        // The desktop's request drops (a tunnel blip, the Mac asleep) while the remote's steer waits on the agent.
        using var box = Client(_box, BoxToken);
        using var dropped = new CancellationTokenSource();
        var sending = box.PostAsync($"/sessions/{sessionId}/send", Json("""{"text":"also this","clientId":"c18"}"""), dropped.Token);
        var steer = await agent.ReadAsync("_session/steering");
        await dropped.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

        // The window retries through the desktop; it joins the remote's send, which the agent then takes.
        await SendTextAsync(socket, $$$"""{"t":"call","id":2,"feature":"send","method":"send","args":{"session":"{{{sessionId}}}","text":"also this","clientId":"c18"}}""");
        await _log.WrittenAsync("send c18: joined in-flight send");
        await agent.ReplyAsync(steer, """{"outcome":"injected"}""");
        Assert.Equal($$$"""{"t":"result","id":2,"result":{"runId":"{{{runId}}}"}}""", await ReadTextAsync(socket));

        // The next thing the agent hears is the turn's close: one steering request.
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        Assert.Single(await RowsAsync(_box, BoxToken, sessionId), row => ClientIdOf(row) == "c18");
    }

    [Fact]
    public async Task The_remote_send_route_refuses_a_bad_clientId()
    {
        var sessionId = await CreateSessionAsync();
        using var http = Client(_box, BoxToken);

        foreach (var bad in new[] { "", new string('a', 65), "a b", "a/b" })
        {
            var answer = await http.PostAsync($"/sessions/{sessionId}/send", Json(JsonSerializer.Serialize(new { text = "hi", clientId = bad })));
            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
            Assert.Equal($$"""{"error":"{{BadClientId}}"}""", await answer.Content.ReadAsStringAsync());
        }
        // A null clientId is no clientId, as on the socket: this one gets as far as the session lookup.
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/sessions/nope/send", Json("""{"text":"hi","clientId":null}"""))).StatusCode);

        var sent = await http.PostAsync($"/sessions/{sessionId}/send", Json(JsonSerializer.Serialize(new { text = "hi", clientId = new string('a', 64) })));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        await RunTurnOnBoxAsync();
    }

    private static async Task<ClientWebSocket> OpenSocketAsync(DesktopBackend backend)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{backend.Address.Authority}/socket"), CancellationToken.None);
        await SendTextAsync(socket, $$"""{"t":"hello","token":"{{DesktopToken}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadTextAsync(socket));
        return socket;
    }

    private static async Task<string> CallSendAsync(WebSocket socket, long id, string sessionId, string text, string clientId)
    {
        var args = JsonSerializer.Serialize(new { session = sessionId, text, clientId });
        await SendTextAsync(socket, $$"""{"t":"call","id":{{id}},"feature":"send","method":"send","args":{{args}}}""");
        return await ReadTextAsync(socket);
    }

    private static string RunOf(string answer) => JsonDocument.Parse(answer).RootElement.GetProperty("result").GetProperty("runId").GetString()!;

    private static string? ClientIdOf(JsonElement row) =>
        row.GetProperty("kind").GetString() == "user" && row.GetProperty("payload").TryGetProperty("clientId", out var id) ? id.GetString() : null;
}
