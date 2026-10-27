using System.Text.Json;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    [Fact]
    public async Task The_sessions_topic_starts_with_the_newest_page_then_any_session_that_changes()
    {
        var made = new List<string>();
        for (var i = 0; i <= SessionsFeature.ListLimit; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            made.Add(_sessions.Create("claude", $"repo-{i}").SessionId);
        }
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"sessions"}""");
        using (var state = JsonDocument.Parse(await ReadAsync(socket)))
        {
            var items = state.RootElement.GetProperty("items").EnumerateArray().Select(entry => entry.GetProperty("key").GetString()).ToList();
            Assert.Equal(Enumerable.Reverse(made).Take(SessionsFeature.ListLimit), items);
        }

        // The oldest, outside the page, wakes up and moves in.
        _time.Advance(TimeSpan.FromSeconds(1));
        _sessions.Touch(made[0]);
        using var change = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("change", change.RootElement.GetProperty("t").GetString());
        Assert.Equal(made[0], change.RootElement.GetProperty("key").GetString());
        Assert.Equal(_time.GetUtcNow().ToUnixTimeMilliseconds(), change.RootElement.GetProperty("item").GetProperty("updatedAt").GetInt64());
    }

    [Fact]
    public async Task A_keyed_sessions_sub_gets_that_session_and_its_changes()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();

        await SendAsync(socket, $$"""{"t":"sub","id":1,"topic":"sessions","key":"{{session.SessionId}}"}""");
        Assert.Contains($"\"sessionId\":\"{session.SessionId}\"", await ReadAsync(socket));

        _sessions.SetTitle(session.SessionId, "Renamed");
        Assert.Contains("\"title\":\"Renamed\"", await ReadAsync(socket));
    }

    [Fact]
    public async Task Sessions_page_reads_older_sessions_before_a_time()
    {
        var made = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            made.Add(_sessions.Create("claude", $"repo-{i}").SessionId);
        }
        var newest = _sessions.Get(made[2])!.UpdatedAt;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "sessions", "page", $$"""{"before":{{newest}},"limit":1}""");

        using var answer = JsonDocument.Parse(await ReadAsync(socket));
        var page = answer.RootElement.GetProperty("result");
        Assert.Equal([made[1]], page.GetProperty("sessions").EnumerateArray().Select(item => item.GetProperty("sessionId").GetString()));
        Assert.Equal(_sessions.Get(made[1])!.UpdatedAt, page.GetProperty("nextBefore").GetInt64());
    }

    // The window already has the new session when it opens it.
    [Fact]
    public async Task Sessions_create_sends_the_new_session_before_its_answer()
    {
        using var socket = await OpenAsync();
        await SendAsync(socket, """{"t":"sub","id":1,"topic":"sessions"}""");
        await ReadAsync(socket);

        await CallAsync(socket, 2, "sessions", "create", """{"agent":"claude","repoUrl":"repo"}""");

        using var change = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("change", change.RootElement.GetProperty("t").GetString());
        var made = change.RootElement.GetProperty("key").GetString();
        Assert.Equal($$$"""{"t":"result","id":2,"result":{"sessionId":"{{{made}}}"}}""", await ReadAsync(socket));
    }

    [Fact]
    public async Task A_down_machine_answers_unreachable_with_its_name()
    {
        _box.Down = true;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "sessions", "create", """{"agent":"claude","repoUrl":"repo","machine":"box"}""");

        Assert.Equal("""{"t":"error","id":1,"code":"unreachable","message":"box is down","machine":"box"}""", await ReadAsync(socket));
    }
}
