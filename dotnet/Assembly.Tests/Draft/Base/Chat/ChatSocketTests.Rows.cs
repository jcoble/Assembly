using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Socket;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    [Fact]
    public async Task Rows_page_has_the_machine_watch_the_session_and_tells_its_active_run()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Running = "run-3";
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "page", $$"""{"session":"{{session.SessionId}}"}""");

        using var answer = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("run-3", answer.RootElement.GetProperty("result").GetProperty("activeRunId").GetString());
        Assert.Equal([$"watch {session.SessionId}"], _box.Asked);
    }

    [Fact]
    public async Task Rows_tool_reads_a_tool_call_from_the_sessions_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Tool = new StoredToolCall("ls", "-la", "a.txt", null, null);
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "tool", $$"""{"session":"{{session.SessionId}}","item":"t1"}""");
        Assert.Equal(
            """{"t":"result","id":1,"result":{"summary":"ls","input":"-la","output":"a.txt","truncated":false,"outputBytes":5,"diffBytes":0}}""",
            await ReadAsync(socket));
        Assert.Equal(["tool t1"], _box.Asked);

        _box.Tool = null;
        await CallAsync(socket, 2, "rows", "tool", $$"""{"session":"{{session.SessionId}}","item":"t2"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
    }

    // A 3 MB output and a diff of two-byte characters: each is cut to a whole character within the cap.
    [Fact]
    public async Task Rows_tool_cuts_a_big_output_and_diff_and_tells_their_full_sizes()
    {
        var session = _sessions.Create("claude", "repo");
        var output = new string('a', 3 * RowsFeature.ToolCap);
        var diff = new string('é', RowsFeature.ToolCap);
        _here.Tool = new StoredToolCall("cat", null, output, diff, null);
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "tool", $$"""{"session":"{{session.SessionId}}","item":"t1"}""");

        using var answer = JsonDocument.Parse(await ReadAsync(socket));
        var detail = answer.RootElement.GetProperty("result");
        Assert.True(detail.GetProperty("truncated").GetBoolean());
        Assert.Equal(output[..RowsFeature.ToolCap], detail.GetProperty("output").GetString());
        Assert.Equal(diff[..(RowsFeature.ToolCap / 2)], detail.GetProperty("diff").GetString());
        Assert.Equal(3 * RowsFeature.ToolCap, detail.GetProperty("outputBytes").GetInt64());
        Assert.Equal(Encoding.UTF8.GetByteCount(diff), detail.GetProperty("diffBytes").GetInt64());
    }

    [Fact]
    public async Task A_cancel_aborts_rows_tool_on_the_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Hold();
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "tool", $$"""{"session":"{{session.SessionId}}","item":"t1"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        await SendAsync(socket, """{"t":"cancel","id":1}""");

        await _box.Cancelled.Task.WaitAsync(Wait);
        Assert.Contains("\"code\":\"cancelled\"", await ReadAsync(socket));
    }

    [Fact]
    public async Task Closing_the_socket_aborts_rows_tool_on_the_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Hold();
        var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "tool", $$"""{"session":"{{session.SessionId}}","item":"t1"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        socket.Abort();
        socket.Dispose();

        await _box.Cancelled.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task Rows_tool_that_runs_over_30_seconds_answers_timeout_and_cancels_the_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Hold();
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "tool", $$"""{"session":"{{session.SessionId}}","item":"t1"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        _time.Advance(SocketDispatcher.Limit);

        await _box.Cancelled.Task.WaitAsync(Wait);
        Assert.Equal("""{"t":"error","id":1,"code":"timeout","message":"rows/tool ran over 30 s"}""", await ReadAsync(socket));
    }

    // Only the output and diff are cut; a 5 MB input makes the answer too big to send.
    [Fact]
    public async Task Rows_tool_whose_answer_is_over_4_MB_answers_failed()
    {
        var session = _sessions.Create("claude", "repo");
        _here.Tool = new StoredToolCall("cat", new string('a', 5 * Mb), null, null, null);
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "tool", $$"""{"session":"{{session.SessionId}}","item":"t1"}""");

        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"the answer is over 4 MB"}""", await ReadAsync(socket));
    }

    [Fact]
    public async Task Rows_page_whose_answer_is_over_4_MB_answers_failed()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        var text = new string('a', Mb);
        for (var i = 0; i < 5; i++)
            Row(session, RowKind.Message, $"m{i}", $$"""{"text":"{{text}}"}""");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "page", $$"""{"session":"{{session}}"}""");

        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"the answer is over 4 MB"}""", await ReadAsync(socket));
    }

    // rows/page reads this machine's copy; only rows/tool asks the session's machine.
    [Fact]
    public async Task Rows_tool_on_a_machine_thats_down_is_unreachable_and_rows_page_still_answers()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Down = true;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "tool", $$"""{"session":"{{session.SessionId}}","item":"t1"}""");
        Assert.Equal("""{"t":"error","id":1,"code":"unreachable","message":"box is down","machine":"box"}""", await ReadAsync(socket));
        await CallAsync(socket, 2, "rows", "page", $$"""{"session":"{{session.SessionId}}"}""");
        Assert.StartsWith("""{"t":"result","id":2,""", await ReadAsync(socket));
    }

    // Pins: g0-shape#13
    // Each call counts once, by its newest row; a read, a command and a failed edit touched nothing.
    [Fact]
    public async Task Rows_files_sums_each_edited_files_lines_newest_first()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        void Tool(string item, string kind, string state, string? path, string lines = "") =>
            Row(session, RowKind.Tool, item,
                $$"""{"name":"{{kind}}","state":"{{state}}","path":{{(path is null ? "null" : $"\"{path}\"")}},"hasDetail":false,"kind":"{{kind}}"{{lines}}}""");
        Tool("t1", "edit", "started", "a.rs");
        Tool("t1", "edit", "completed", "a.rs", ""","added":3,"removed":1""");
        Tool("t2", "read", "completed", "b.rs");
        Tool("t3", "edit", "completed", "a.rs", ""","added":2""");
        Tool("t4", "delete", "completed", "c.rs");
        Tool("t5", "edit", "failed", "d.rs", ""","added":1""");
        Tool("t6", "execute", "completed", null);
        Tool("t7", "move", "completed", "e.rs");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "files", $$"""{"session":"{{session}}"}""");
        Assert.Equal("""{"t":"result","id":1,"result":[{"path":"e.rs","added":0,"removed":0,"lastSeq":8},""" +
            """{"path":"c.rs","added":0,"removed":0,"lastSeq":5},{"path":"a.rs","added":5,"removed":1,"lastSeq":4}]}""",
            await ReadAsync(socket));
        await CallAsync(socket, 2, "rows", "files", """{"session":"nope"}""");
        Assert.Equal("""{"t":"error","id":2,"code":"failed","message":"unknown session"}""", await ReadAsync(socket));
    }

    // A page of an unknown session fails, as a tool call of an unknown session or item does.
    [Fact]
    public async Task Rows_page_and_tool_for_an_unknown_session_or_item()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "page", """{"session":"nope"}""");
        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"unknown session"}""", await ReadAsync(socket));
        await CallAsync(socket, 2, "rows", "tool", """{"session":"nope","item":"t1"}""");
        Assert.Equal("""{"t":"error","id":2,"code":"failed","message":"unknown tool call"}""", await ReadAsync(socket));
        await CallAsync(socket, 3, "rows", "tool", $$"""{"session":"{{session.SessionId}}","item":"t9"}""");
        Assert.Equal("""{"t":"error","id":3,"code":"failed","message":"unknown tool call"}""", await ReadAsync(socket));

        Assert.Equal(["tool t9"], _here.Asked);
        Assert.Empty(_box.Asked);
    }
}
