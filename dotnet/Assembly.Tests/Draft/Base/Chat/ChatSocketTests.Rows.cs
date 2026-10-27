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
}
