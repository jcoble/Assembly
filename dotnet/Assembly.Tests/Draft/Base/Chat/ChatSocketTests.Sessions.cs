using System.Text.Json;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    // Pins: sessions-list#9
    [Fact]
    public async Task Sessions_state_is_open_and_pages_can_select_settled()
    {
        var open = _sessions.Create("claude", "open-repo");
        var settled = _sessions.Create("claude", "settled-repo");
        _db.Writer.Write("test.mark", scope => { _sessions.Mark(scope, settled.SessionId, "settled"); return 0; });
        using var socket = await OpenAsync();
        await SendAsync(socket, """{"t":"sub","id":1,"topic":"sessions"}""");
        using (var state = JsonDocument.Parse(await ReadAsync(socket)))
            Assert.Equal([open.SessionId], state.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("key").GetString()));

        await CallAsync(socket, 2, "sessions", "page", """{"group":"settled"}""");
        using (var answer = JsonDocument.Parse(await ReadAsync(socket)))
            Assert.Equal([settled.SessionId], answer.RootElement.GetProperty("result").GetProperty("sessions").EnumerateArray()
                .Select(item => item.GetProperty("sessionId").GetString()));
        await CallAsync(socket, 3, "sessions", "page", """{"group":"bogus"}""");
        Assert.Equal("""{"t":"error","id":3,"code":"failed","message":"expected open or settled"}""", await ReadAsync(socket));

        _sessions.SetTitle(settled.SessionId, "Still published", "user");
        Assert.Contains("\"title\":\"Still published\"", await ReadAsync(socket));
    }

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

        _sessions.SetTitle(session.SessionId, "Renamed", "prompt");
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

    // Escaped, an accent or apostrophe takes six bytes; a big answer of such text would pass the 4 MB frame limit.
    [Fact]
    public async Task Frames_carry_text_as_it_is()
    {
        var session = _sessions.Create("claude", "repo");
        _sessions.SetTitle(session.SessionId, "Café's", "prompt");
        using var socket = await OpenAsync();

        await SendAsync(socket, $$"""{"t":"sub","id":1,"topic":"sessions","key":"{{session.SessionId}}"}""");
        Assert.Contains("\"title\":\"Café's\"", await ReadAsync(socket));
        await CallAsync(socket, 2, "sessions", "page", "{}");
        Assert.Contains("\"title\":\"Café's\"", await ReadAsync(socket));
    }

    // The list's state has no helpers, so its changes don't either; a helper's own sub still gets them.
    [Fact]
    public async Task A_helpers_changes_go_only_to_its_own_sub()
    {
        var parent = _sessions.Create("claude", "repo");
        using var list = await OpenAsync();
        await SendAsync(list, """{"t":"sub","id":1,"topic":"sessions"}""");
        await ReadAsync(list);

        var helper = _sessions.Helper(parent, "claude", "Helper");
        using var own = await OpenAsync();
        await SendAsync(own, $$"""{"t":"sub","id":1,"topic":"sessions","key":"{{helper.SessionId}}"}""");
        await ReadAsync(own);
        _sessions.SetTitle(helper.SessionId, "Renamed", "prompt");
        _sessions.SetTitle(parent.SessionId, "Parent", "prompt");

        Assert.Contains("\"title\":\"Renamed\"", await ReadAsync(own));
        using var change = JsonDocument.Parse(await ReadAsync(list));
        Assert.Equal(parent.SessionId, change.RootElement.GetProperty("key").GetString());
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

    [Fact]
    public async Task Sessions_create_fails_for_an_unknown_machine_or_an_agent_the_machine_doesnt_have()
    {
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "sessions", "create", """{"agent":"claude","repoUrl":"repo","machine":"nope"}""");
        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"unknown machine"}""", await ReadAsync(socket));
        await CallAsync(socket, 2, "sessions", "create", """{"agent":"codex","repoUrl":"repo"}""");
        Assert.Equal("""{"t":"error","id":2,"code":"failed","message":"this machine has no agent codex"}""", await ReadAsync(socket));

        Assert.Equal(["agents"], _here.Asked);
        Assert.Empty(_box.Asked);
    }

    [Fact]
    public async Task Sessions_create_with_a_parent_makes_a_helper_with_its_title_or_Helper()
    {
        var parent = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "sessions", "create",
            $$"""{"agent":"claude","repoUrl":"repo","parentSessionId":"{{parent.SessionId}}","title":"Look"}""");
        var titled = _sessions.Get(Result(await ReadAsync(socket)).GetProperty("sessionId").GetString()!)!;
        await CallAsync(socket, 2, "sessions", "create", $$"""{"agent":"claude","repoUrl":"repo","parentSessionId":"{{parent.SessionId}}"}""");
        var untitled = _sessions.Get(Result(await ReadAsync(socket)).GetProperty("sessionId").GetString()!)!;

        Assert.Equal((parent.SessionId, true, "Look"), (titled.ParentSessionId, titled.Helper, titled.Title));
        Assert.Equal((parent.SessionId, true, "Helper"), (untitled.ParentSessionId, untitled.Helper, untitled.Title));
        Assert.Equal(["agents", "helper", "agents", "helper"], _here.Asked);
    }
}
