using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Draft.Base.Chat;

// A write that waits on a machine queues behind that session's (or for a create, that machine's) calls only, so a
// machine that hangs holds up nothing else on the window's one socket.
public sealed partial class ChatSocketTests
{
    private const string Save = """{"key":"theme","value":"dark"}""";

    [Fact]
    public async Task Other_sessions_and_saves_answer_while_a_send_hangs_on_its_machine()
    {
        var hung = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        var other = _sessions.Create("claude", "repo");
        var turn = new RowBatch();
        turn.Rows.Add(new NewRow("run-9", null, RowKind.Turn, JsonDocument.Parse("""{"state":"started"}""").RootElement.Clone(), 1));
        _hub.Publish(other.SessionId, turn);
        _box.Hold();
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "send", $$"""{"session":"{{hung.SessionId}}","text":"hi"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        await CallAsync(socket, 2, "send", "send", $$"""{"session":"{{other.SessionId}}","text":"hi"}""");
        await CallAsync(socket, 3, "send", "stop", """{"runId":"run-9"}""");
        await CallAsync(socket, 4, "preferences", "set", Save);

        var answers = await ReadAnswersAsync(socket, 3);
        Assert.Equal([2, 3, 4], answers.Keys.Order());
        Assert.All(answers.Values, answer => Assert.Contains("\"t\":\"result\"", answer));
        _box.Release();
        Assert.Contains("\"runId\":\"run-1\"", (await ReadAnswersAsync(socket, 1))[1]);
    }

    [Fact]
    public async Task Sends_to_one_session_reach_its_machine_in_order()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Hold();
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"one"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        await CallAsync(socket, 2, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"two"}""");
        // Frames are read in order, so once the save behind it answers, the second send has been queued.
        await CallAsync(socket, 3, "preferences", "set", Save);
        Assert.Equal([3], (await ReadAnswersAsync(socket, 1)).Keys);
        Assert.Equal([$"send {session.SessionId} one"], _box.Asked);

        _box.Release();
        Assert.Equal([1, 2], (await ReadAnswersAsync(socket, 2)).Keys.Order());
        Assert.Equal([$"send {session.SessionId} one", $"send {session.SessionId} two"], _box.Asked);
    }

    [Fact]
    public async Task A_create_on_a_hung_machine_doesnt_hold_up_a_save()
    {
        _box.Hold();
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "sessions", "create", """{"agent":"claude","repoUrl":"repo","machine":"box"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        await CallAsync(socket, 2, "preferences", "set", Save);

        Assert.Equal([2], (await ReadAnswersAsync(socket, 1)).Keys);
        _box.Release();
        Assert.Equal([1], (await ReadAnswersAsync(socket, 1)).Keys);
    }

    [Fact]
    public async Task A_socket_that_closes_still_runs_the_sends_it_queued()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        var local = _sessions.Create("claude", "repo");
        _box.Hold();
        _here.Hold();
        var socket = await OpenAsync();

        // A read is cancelled by its socket closing, and a closing socket cancels in the reverse order the calls came,
        // so once this one, sent first, is cancelled, the backend has seen the close and would have cancelled the sends.
        await CallAsync(socket, 1, "config", "get", $$"""{"session":"{{local.SessionId}}"}""");
        await _here.Reached.Task.WaitAsync(Wait);
        await CallAsync(socket, 2, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"one"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        await CallAsync(socket, 3, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"two"}""");
        // Frames are read in order, so once the save behind it answers, the second send has been queued; an abort
        // sooner could land before the backend reads it.
        await CallAsync(socket, 4, "preferences", "set", Save);
        Assert.Equal([4], (await ReadAnswersAsync(socket, 1)).Keys);
        socket.Abort();
        socket.Dispose();
        await _here.Cancelled.Task.WaitAsync(Wait);
        _box.Release();

        Assert.True(SpinWait.SpinUntil(() => { lock (_box.Asked) return _box.Asked.Count == 2; }, Wait));
        Assert.Equal($"send {session.SessionId} two", _box.Asked[1]);
    }

    // The next count answers (result or error frames), by id; other frames are skipped.
    private static async Task<Dictionary<long, string>> ReadAnswersAsync(WebSocket socket, int count)
    {
        var answers = new Dictionary<long, string>();
        while (answers.Count < count)
        {
            var frame = await ReadAsync(socket);
            using var json = JsonDocument.Parse(frame);
            if (json.RootElement.GetProperty("t").GetString() is "result" or "error")
                answers[json.RootElement.GetProperty("id").GetInt64()] = frame;
        }
        return answers;
    }
}
