using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
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
        using var socket = await OpenBulkAsync();

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
        using var socket = await OpenBulkAsync();

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
        _here.Tool = new StoredToolCall("cat", new string('a', 5 * Mb), null, null, null);
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

    // Pins: g0-shape#13
    // A patch that changes two files lists each with its own lines, not the first with all of them.
    [Fact]
    public async Task Rows_files_lists_each_file_one_call_changed()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        var tool = new OpenTool("t1");
        tool.Apply(new ToolCallFields(Name: "apply_patch", Kind: "edit", Status: "completed",
            Diffs: [new FileDiff("a.rs", "x", "x\ny\nz"), new FileDiff("b.rs", null, "1\n2\n3")]));
        Row(session, RowKind.Tool, "t1", JsonSerializer.Serialize(tool.TakeChanges().Line, RowJsonContext.Rows.ToolPayload));
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "files", $$"""{"session":"{{session}}"}""");

        Assert.Equal("""{"t":"result","id":1,"result":[{"path":"a.rs","added":2,"removed":0,"lastSeq":1},""" +
            """{"path":"b.rs","added":3,"removed":0,"lastSeq":1}]}""", await ReadAsync(socket));
    }

    // Pins: g0-shape#13
    // The read runs on the call's cancellation, so a read the window drops stops in SQLite rather than running on.
    [Fact]
    public void Rows_files_stops_once_its_read_is_cancelled()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        Assert.Throws<OperationCanceledException>(() => _rows.FilesTouched(session, new CancellationToken(canceled: true)));
    }

    // Pins: g0-shape#14
    // A prompt names its images in trailing "Attached image:" lines; the prompts that have them come back, newest first,
    // with the machine that has the images when it isn't this one.
    [Fact]
    public async Task Rows_attachments_lists_the_prompts_naming_images_newest_first_and_the_machine()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        Row(session, RowKind.User, null, """{"text":"look\nAttached image: /a/1.png (one.png)"}""");
        Row(session, RowKind.User, null, """{"text":"no images here"}""");
        Row(session, RowKind.User, null, """{"text":"both\nAttached image: /a/2.png (two.png)\nAttached image: /a/3.png (three.png)"}""");
        var remote = _sessions.Add("r1", "claude", "repo", "Workbox Test").SessionId;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "attachments", $$"""{"session":"{{session}}"}""");
        Assert.Equal("""{"t":"result","id":1,"result":{"prompts":["both\nAttached image: /a/2.png (two.png)\nAttached image: /a/3.png (three.png)","look\nAttached image: /a/1.png (one.png)"],"more":false}}""", await ReadAsync(socket));
        await CallAsync(socket, 2, "rows", "attachments", $$"""{"session":"{{remote}}"}""");
        Assert.Equal("""{"t":"result","id":2,"result":{"machine":"Workbox Test","prompts":[],"more":false}}""", await ReadAsync(socket));
        await CallAsync(socket, 3, "rows", "attachments", """{"session":"nope"}""");
        Assert.Equal("""{"t":"error","id":3,"code":"failed","message":"unknown session"}""", await ReadAsync(socket));
    }

    // Pins: g0-shape#14
    // The answer stays inside the socket's 4 MB cap: each prompt's end only, where its image lines are, and the newest
    // AttachmentPrompts prompts, with more set when older ones were left out.
    [Fact]
    public async Task Rows_attachments_sends_each_prompts_end_and_at_most_the_newest_fifty()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        var line = "Attached image: /a/big.png (big.png)";
        Row(session, RowKind.User, null, JsonSerializer.Serialize(new { text = new string('x', 20_000) + "\n" + line }));
        for (var i = 0; i < RowsFeature.AttachmentPrompts; i++)
            Row(session, RowKind.User, null, $$"""{"text":"Attached image: /a/{{i}}.png ({{i}}.png)"}""");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "rows", "attachments", $$"""{"session":"{{session}}"}""");
        var answer = JsonDocument.Parse(await ReadAsync(socket)).RootElement.GetProperty("result");
        var prompts = answer.GetProperty("prompts").EnumerateArray().Select(prompt => prompt.GetString()!).ToList();
        Assert.Equal(RowsFeature.AttachmentPrompts, prompts.Count);
        Assert.Equal($"Attached image: /a/{RowsFeature.AttachmentPrompts - 1}.png ({RowsFeature.AttachmentPrompts - 1}.png)", prompts[0]);
        Assert.True(answer.GetProperty("more").GetBoolean());

        Row(session, RowKind.User, null, JsonSerializer.Serialize(new { text = new string('x', 20_000) + "\n" + line }));
        await CallAsync(socket, 2, "rows", "attachments", $$"""{"session":"{{session}}"}""");
        var newest = JsonDocument.Parse(await ReadAsync(socket)).RootElement.GetProperty("result").GetProperty("prompts")[0].GetString()!;
        Assert.Equal(RowsFeature.AttachmentTail, newest.Length);
        Assert.EndsWith("\n" + line, newest);
    }

    // A page of an unknown session fails, as a tool call of an unknown session or item does.
    [Fact]
    public async Task Rows_page_and_tool_for_an_unknown_session_or_item()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenBulkAsync();

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
