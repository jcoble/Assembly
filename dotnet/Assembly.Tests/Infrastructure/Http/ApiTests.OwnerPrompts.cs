using System.Text.Json;
using System.Net.WebSockets;

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>Committed owner acceptance remains distinct from the actual run pushed by rows.</summary>
public sealed partial class ApiTests
{
    [Fact]
    public async Task A_sent_turn_shows_on_the_socket_and_in_pages_and_a_second_send_waits()
    {
        var sessionId = await CreateSessionAsync();
        using var socket = await OpenRowsAsync(_base, Token, sessionId, after: 0);
        var clientId = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.Serialize(new { session = sessionId, text = "hi", clientId });
        var accepted = await ResultAsync("send", "send", request);
        AssertQueuedAcceptance(accepted, sessionId, clientId);
        Assert.Equal("failed", (await RequestAsync("send", "send", """{"session":"nope","text":"hi"}""")).GetProperty("code").GetString());
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(canSteer: false);
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "first-inserted");
        var started = await ReadRowsAsync(socket, 2);
        var runId = RunOfPrompt(started, sessionId, clientId);
        var page = await ResultAsync("rows", "page", JsonSerializer.Serialize(new { session = sessionId }), bulk: true);
        Assert.Equal(runId, page.GetProperty("activeRunId").GetString());
        Assert.False(page.GetProperty("hasBefore").GetBoolean());
        Assert.Equal(["user", "turn"], page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("kind").GetString()));
        var nextClient = await QueueAsync(sessionId, "again");
        var waiting = await ResultAsync("rows", "page", JsonSerializer.Serialize(new { session = sessionId }), true);
        Assert.Equal(runId, waiting.GetProperty("activeRunId").GetString());
        Assert.Equal(["user", "turn"], waiting.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("kind").GetString()));
        await agent.TextAsync("Hello");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", prompt.GetProperty("params").GetProperty("sessionId").GetString()!);
        var rows = started.Concat(await ReadRowsAsync(socket, 4)).ToList();
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], rows.Select(row => row.GetProperty("seq").GetInt64()));
        Assert.Equal(["user", "turn", "delta", "message", "stats", "turn"], rows.Select(row => row.GetProperty("kind").GetString()));
        Assert.Equal(sessionId, rows[0].GetProperty("sessionId").GetString());
        Assert.Equal(runId, rows[5].GetProperty("runId").GetString());

        var after = await ResultAsync("rows", "page", JsonSerializer.Serialize(new { session = sessionId }), bulk: true);
        Assert.Equal(["user", "turn", "message", "stats", "turn"], after.GetProperty("rows").EnumerateArray()
            .Where(row => row.GetProperty("runId").GetString() == runId).Select(row => row.GetProperty("kind").GetString()));
        Assert.Equal(accepted.GetRawText(), (await ResultAsync("send", "send", request)).GetRawText());
        await agent.CloseAsync();
        await agent.OpenAsync();
        var next = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(next, "next-inserted");
        Assert.Equal("again", next.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        var nextRows = await ReadRowsAsync(socket, 2);
        Assert.NotEqual(runId, RunOfPrompt(nextRows, sessionId, nextClient));
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", next.GetProperty("params").GetProperty("sessionId").GetString()!);
        await ReadUntilTurnEndsAsync(socket);
        await agent.CloseAsync();
        Assert.Equal(JsonValueKind.Null, (await ResultAsync("rows", "page", JsonSerializer.Serialize(new { session = sessionId }), true)).GetProperty("activeRunId").ValueKind);
    }

    private async Task<string> QueueAsync(string session, string text = "hi")
    {
        var clientId = Guid.NewGuid().ToString("N");
        AssertQueuedAcceptance(await ResultAsync("send", "send", JsonSerializer.Serialize(new { session, text, clientId })), session, clientId);
        return clientId;
    }
    internal static async Task<string> QueueSocketTurnAsync(WebSocket socket, string sessionId)
    {
        var clientId = Guid.NewGuid().ToString("N");
        var result = Result(await SocketRequestAsync(socket, 2, "send", "send",
            JsonSerializer.Serialize(new { session = sessionId, text = "hi", clientId })));
        AssertQueuedAcceptance(result, sessionId, clientId);
        return clientId;
    }

    internal static void AssertQueuedAcceptance(JsonElement accepted, string session, string clientId)
    {
        Assert.Equal(session, accepted.GetProperty("sessionId").GetString());
        Assert.Equal($"prompt/{session}/{clientId}", accepted.GetProperty("messageId").GetString());
        Assert.Equal(clientId, accepted.GetProperty("clientId").GetString());
        Assert.Equal("queued", accepted.GetProperty("state").GetString());
        Assert.True(accepted.GetProperty("messageVersion").GetInt64() > 0);
        Assert.True(accepted.GetProperty("seq").GetInt64() > 0);
        Assert.True(accepted.GetProperty("epoch").GetInt64() >= 0);
        Assert.Equal(JsonValueKind.Null, accepted.GetProperty("runId").ValueKind);
    }
    internal static string RunOfPrompt(IEnumerable<JsonElement> rows, string session, string clientId)
    {
        var user = Assert.Single(rows, row => row.GetProperty("kind").GetString() == "user" &&
            row.GetProperty("payload").GetProperty("clientId").GetString() == clientId);
        Assert.Equal(session, user.GetProperty("sessionId").GetString());
        var runId = user.GetProperty("runId").GetString();
        Assert.False(string.IsNullOrEmpty(runId));
        return runId!;
    }
}
