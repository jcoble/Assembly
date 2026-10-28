using System.Text.Json;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    // Pins: sessions-list#2, sessions-list#4, sessions-list#5
    [Fact]
    public async Task A_mark_publishes_its_times_only_when_the_mark_changes()
    {
        var session = _sessions.Add("remote-session", "agent", "repo", "box").SessionId;
        using var socket = await OpenAsync();
        await SubscribeSessionsAsync(socket);

        await CallAsync(socket, 2, "sessions", "mark", $$"""{"session":"{{session}}","state":"done"}""");
        var done = Assert.Single(await ChangesAsync(socket, 2));
        var completed = _time.GetUtcNow().ToUnixTimeMilliseconds();
        Assert.Equal(session, Text(done, "sessionId"));
        Assert.Equal(completed, done.GetProperty("completedAt").GetInt64());
        Assert.Equal(JsonValueKind.Null, done.GetProperty("settledAt").ValueKind);

        _time.Advance(TimeSpan.FromSeconds(1));
        await CallAsync(socket, 3, "sessions", "mark", $$"""{"session":"{{session}}","state":"done"}""");
        Assert.Empty(await ChangesAsync(socket, 3));
        await CallAsync(socket, 4, "sessions", "mark", $$"""{"session":"{{session}}","state":"settled"}""");
        var settled = Assert.Single(await ChangesAsync(socket, 4));
        Assert.Equal(completed, settled.GetProperty("completedAt").GetInt64());
        Assert.Equal(_time.GetUtcNow().ToUnixTimeMilliseconds(), settled.GetProperty("settledAt").GetInt64());
        await CallAsync(socket, 5, "sessions", "mark", $$"""{"session":"{{session}}","state":"settled"}""");
        Assert.Empty(await ChangesAsync(socket, 5));
        await CallAsync(socket, 6, "sessions", "mark", $$"""{"session":"{{session}}","state":"working"}""");
        var working = Assert.Single(await ChangesAsync(socket, 6));
        Assert.Equal(JsonValueKind.Null, working.GetProperty("completedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, working.GetProperty("settledAt").ValueKind);
        await CallAsync(socket, 7, "sessions", "mark", $$"""{"session":"{{session}}","state":"working"}""");
        Assert.Empty(await ChangesAsync(socket, 7));

        await CallAsync(socket, 8, "sessions", "mark", """{"session":"missing","state":"done"}""");
        Assert.Equal("""{"t":"error","id":8,"code":"failed","message":"unknown session"}""", await ReadAsync(socket));
        await CallAsync(socket, 9, "sessions", "mark", $$"""{"session":"{{session}}","state":"other"}""");
        Assert.Equal("""{"t":"error","id":9,"code":"failed","message":"expected working, done or settled"}""", await ReadAsync(socket));
    }
}
