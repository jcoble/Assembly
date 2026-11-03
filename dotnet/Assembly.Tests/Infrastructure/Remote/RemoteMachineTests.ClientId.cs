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
    public async Task A_send_retried_after_a_restart_replays_its_committed_ack_and_starts_no_turn()
    {
        var created = await FeatureResultAsync(_desktop!, DesktopToken, "sessions", "create",
            new { agent = "claude", repoUrl = "https://github.com/o/r" });
        var sessionId = created.GetProperty("sessionId").GetString()!;
        using var rows = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, 0);
        JsonElement accepted;
        using (var socket = await OpenSocketAsync(_desktop!))
            accepted = await CallAcceptedAsync(socket, 1, sessionId, "hi", "c1");
        var agent = await _desktopLauncher.NextAsync();
        await agent.StartAsync();
        await agent.FinishAsync(await agent.ReadAsync("session/prompt"), """{"stopReason":"end_turn"}""");
        var runId = ApiTests.RunOfPrompt(await ApiTests.ReadUntilTurnEndsAsync(rows), sessionId, "c1");
        rows.Dispose();
        await agent.CloseAsync();

        await _desktop!.DisposeAsync();
        await StartDesktopAsync();
        using var again = await OpenSocketAsync(_desktop);
        Assert.Equal(accepted.GetRawText(), (await CallAcceptedAsync(again, 1, sessionId, "hi", "c1")).GetRawText());

        // The next message is the first prompt the restarted agent sees: the retry started no turn.
        await CallAcceptedAsync(again, 2, sessionId, "two", "c2");
        var restarted = await _desktopLauncher.NextAsync();
        await restarted.StartAsync();
        var prompt = await restarted.ReadAsync("session/prompt");
        Assert.Equal("two", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await restarted.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await restarted.CloseAsync();
        var original = Assert.Single(await RowsAsync(_desktop, DesktopToken, sessionId), row => ClientIdOf(row) == "c1");
        Assert.Equal(runId, original.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task A_send_retried_through_the_desktop_replays_its_committed_ack_with_one_prompt_on_the_remote()
    {
        var sessionId = await CreateSessionAsync();
        using var socket = await OpenSocketAsync(_desktop!);
        var accepted = await CallAcceptedAsync(socket, 1, sessionId, "hi", "c5");
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");

        Assert.Equal(accepted.GetRawText(), (await CallAcceptedAsync(socket, 2, sessionId, "hi", "c5")).GetRawText());

        // The next thing the remote's agent hears is the turn's close: no second prompt or steer.
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        using var desktop = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);
        var streamed = await ApiTests.ReadUntilTurnEndsAsync(desktop);
        var user = Assert.Single(streamed, row => row.GetProperty("kind").GetString() == "user");
        Assert.Equal("c5", ClientIdOf(user));
        ApiTests.RunOfPrompt(streamed, sessionId, "c5");
    }

    [Fact]
    public async Task A_resend_through_the_desktop_with_other_text_fails_with_the_remotes_words()
    {
        var sessionId = await CreateSessionAsync();
        using var socket = await OpenSocketAsync(_desktop!);
        await CallAcceptedAsync(socket, 1, sessionId, "hi", "c16");
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");

        var failed = await CallSendAsync(socket, 2, sessionId, "bye", "c16");

        Assert.StartsWith("""{"t":"error","id":2,"code":"failed",""", failed);
        Assert.Equal("client_id_conflict", JsonDocument.Parse(failed).RootElement.GetProperty("message").GetString());
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_send_whose_owner_socket_drops_after_acceptance_still_lands_and_its_retry_replays_the_same_ack()
    {
        var sessionId = await CreateSessionAsync();
        using var socket = await OpenSocketAsync(_desktop!);
        await CallAcceptedAsync(socket, 1, sessionId, "hi", "c17");
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "retry-prompt");

        // Commit acceptance is independent of the provider's held delivery. Dropping its socket cannot revoke it.
        using var box = await ApiTests.OpenOwnerSocketAsync(_box.Address, BoxToken);
        var accepted = await CallAcceptedAsync(box, 1, sessionId, "also this", "c18");
        var steer = await agent.ReadAsync("_session/steering");
        box.Abort();
        Assert.Equal(WebSocketState.Aborted, box.State);

        // The desktop retry replays that immutable ACK before the provider answers, without another delivery.
        Assert.Equal(accepted.GetRawText(), (await CallAcceptedAsync(socket, 2, sessionId, "also this", "c18")).GetRawText());
        await agent.ReplyAsync(steer, """{"outcome":"injected"}""");

        // The next thing the agent hears is the turn's close: one steering request.
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        await agent.CloseAsync();
        var delivered = await RowsAsync(_box, BoxToken, sessionId);
        Assert.Single(delivered, row => ClientIdOf(row) == "c18");
        Assert.Equal(ApiTests.RunOfPrompt(delivered, sessionId, "c17"), ApiTests.RunOfPrompt(delivered, sessionId, "c18"));
    }

    [Fact]
    public async Task The_remote_send_socket_refuses_a_bad_clientId()
    {
        var sessionId = await CreateSessionAsync();
        using var socket = await ApiTests.OpenOwnerSocketAsync(_box.Address, BoxToken);
        long id = 0;

        foreach (var bad in new[] { "", new string('a', 65), "a b", "a/b" })
        {
            var answer = JsonDocument.Parse(await CallSendAsync(socket, ++id, sessionId, "hi", bad)).RootElement;
            Assert.Equal("error", answer.GetProperty("t").GetString());
            Assert.Equal("failed", answer.GetProperty("code").GetString());
            Assert.Equal(BadClientId, answer.GetProperty("message").GetString());
        }
        // A null clientId is no clientId, as on the socket: this one gets as far as the session lookup.
        var unknown = JsonDocument.Parse(await CallSendAsync(socket, ++id, "nope", "hi", null)).RootElement;
        Assert.Equal("error", unknown.GetProperty("t").GetString());
        Assert.Equal("failed", unknown.GetProperty("code").GetString());
        Assert.Equal("unknown session", unknown.GetProperty("message").GetString());

        await CallAcceptedAsync(socket, ++id, sessionId, "hi", new string('a', 64));
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

    private static async Task<string> CallSendAsync(WebSocket socket, long id, string sessionId, string text, string? clientId)
    {
        var args = JsonSerializer.Serialize(new { session = sessionId, text, clientId });
        await SendTextAsync(socket, $$"""{"t":"call","id":{{id}},"feature":"send","method":"send","args":{{args}}}""");
        return await ReadTextAsync(socket);
    }

    private static async Task<JsonElement> CallAcceptedAsync(WebSocket socket, long id, string sessionId, string text, string clientId)
    {
        var frame = JsonDocument.Parse(await CallSendAsync(socket, id, sessionId, text, clientId)).RootElement;
        Assert.Equal(id, frame.GetProperty("id").GetInt64());
        var accepted = ApiTests.Result(frame);
        ApiTests.AssertQueuedAcceptance(accepted, sessionId, clientId);
        return accepted;
    }

    private static string? ClientIdOf(JsonElement row) =>
        row.GetProperty("kind").GetString() == "user" && row.GetProperty("payload").TryGetProperty("clientId", out var id) ? id.GetString() : null;
}
