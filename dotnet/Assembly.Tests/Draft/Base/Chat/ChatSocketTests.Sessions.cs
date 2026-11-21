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

    // The window already has the new session when it opens it.
    [Fact]
    public async Task Sessions_create_sends_the_new_session_before_its_answer()
    {
        using var socket = await OpenAsync();
        await SendAsync(socket, """{"t":"sub","id":1,"topic":"sessions"}""");
        await ReadAsync(socket);

        await CallAsync(socket, 2, "sessions", "create", JsonSerializer.Serialize(
            new SessionsFeature.CreateArgs("claude", "published_create", ExpectedSource: new(_engine.Machine, 0, null)),
            SessionsJson.Default.CreateArgs));

        using var change = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("change", change.RootElement.GetProperty("t").GetString());
        var made = change.RootElement.GetProperty("key").GetString();
        var frame = await ReadAsync(socket);
        while (true)
        {
            using var pushed = JsonDocument.Parse(frame);
            if (pushed.RootElement.GetProperty("t").GetString() != "change") break;
            Assert.Equal(1, pushed.RootElement.GetProperty("sub").GetInt32());
            Assert.Equal(made, pushed.RootElement.GetProperty("key").GetString());
            frame = await ReadAsync(socket);
        }
        using var answer = JsonDocument.Parse(frame);
        Assert.Equal("result", answer.RootElement.GetProperty("t").GetString());
        Assert.Equal(2, answer.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(made, answer.RootElement.GetProperty("result").GetProperty("sessionId").GetString());
        Assert.Equal(JsonValueKind.Null, answer.RootElement.GetProperty("result").GetProperty("checkoutOperationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, answer.RootElement.GetProperty("result").GetProperty("projectId").ValueKind);
    }

    [Fact]
    public async Task A_down_machine_answers_unreachable_with_its_name()
    {
        _box.Down = true;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "sessions", "create", """{"agent":"claude","clientId":"down_create","machine":"box"}""");

        Assert.Equal("""{"t":"error","id":1,"code":"unreachable","message":"box is down","machine":"box"}""", await ReadAsync(socket));
    }

    // Copied databases give sessions the same time; a page break between them loses neither.
    [Fact]
    public async Task Sessions_page_keeps_sessions_that_share_a_time()
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        var made = Enumerable.Range(0, 3).Select(i => _sessions.Create("claude", $"repo-{i}").SessionId).ToList();
        using var socket = await OpenAsync();

        var seen = new List<string?>();
        var cursor = "";
        for (var id = 1; id <= 3; id++)
        {
            await CallAsync(socket, id, "sessions", "page", $$"""{"limit":1{{cursor}}}""");
            using var answer = JsonDocument.Parse(await ReadAsync(socket));
            var page = answer.RootElement.GetProperty("result");
            seen.AddRange(page.GetProperty("sessions").EnumerateArray().Select(item => item.GetProperty("sessionId").GetString()));
            cursor = $$""","before":{{page.GetProperty("nextBefore").GetInt64()}},"beforeId":"{{page.GetProperty("nextBeforeId").GetString()}}" """.TrimEnd();
        }

        Assert.Equal(made.Order(), seen.Order());
    }

    [Fact]
    public async Task Sessions_page_whose_answer_is_over_4_MB_answers_failed()
    {
        var title = new string('a', Mb);
        for (var i = 0; i < 5; i++)
            _sessions.SetTitle(_sessions.Create("claude", $"repo-{i}").SessionId, title, "prompt");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "sessions", "page", "{}");

        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"the answer is over 4 MB"}""", await ReadAsync(socket));
    }
}
