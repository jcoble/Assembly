using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Socket;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    // A 3 MB output and a diff of two-byte characters: each is cut to a whole character within the cap.
    [Fact]
    public async Task Rows_tool_cuts_a_big_output_and_diff_and_tells_their_full_sizes()
    {
        var session = _sessions.Create("claude", "repo");
        var output = new string('a', 3 * RowsFeature.ToolCap);
        var diff = new string('é', RowsFeature.ToolCap);
        _here.Tool = new StoredToolCall("cat", null, output, diff, null, Encoding.UTF8.GetByteCount(output), Encoding.UTF8.GetByteCount(diff));
        using var socket = await OpenBulkAsync();

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
        using var socket = await OpenBulkAsync();

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
        var socket = await OpenBulkAsync();

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
        using var socket = await OpenBulkAsync();

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
        _here.Tool = new StoredToolCall("cat", new string('a', 5 * Mb), null, null, null, 0, 0);
        using var socket = await OpenBulkAsync();

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
        using var socket = await OpenBulkAsync();

        await CallAsync(socket, 1, "rows", "page", $$"""{"session":"{{session}}"}""");

        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"the answer is over 4 MB"}""", await ReadAsync(socket));
    }

    // rows/page reads this machine's copy; only rows/tool asks the session's machine.
    [Fact]
    public async Task Rows_tool_on_a_machine_thats_down_is_unreachable_and_rows_page_still_answers()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Down = true;
        using var socket = await OpenBulkAsync();

        await CallAsync(socket, 1, "rows", "tool", $$"""{"session":"{{session.SessionId}}","item":"t1"}""");
        Assert.Equal("""{"t":"error","id":1,"code":"unreachable","message":"box is down","machine":"box"}""", await ReadAsync(socket));
        await CallAsync(socket, 2, "rows", "page", $$"""{"session":"{{session.SessionId}}"}""");
        Assert.StartsWith("""{"t":"result","id":2,""", await ReadAsync(socket));
    }

    // Pins: g0-shape#13
    // The read runs on the call's cancellation, so a read the window drops stops in SQLite rather than running on.
    [Fact]
    public void Rows_files_stops_once_its_read_is_cancelled()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        Assert.Throws<OperationCanceledException>(() => _rows.FilesTouched(session, new CancellationToken(canceled: true)));
    }
}
