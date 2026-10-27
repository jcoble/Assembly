using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    [Fact]
    public async Task Send_starts_a_turn_on_the_sessions_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"hi"}""");

        Assert.Equal("""{"t":"result","id":1,"result":{"runId":"run-1"}}""", await ReadAsync(socket));
        Assert.Equal([$"send {session.SessionId} hi"], _box.Asked);
        Assert.Empty(_here.Asked);
    }

    [Fact]
    public async Task Send_fails_when_the_turn_cant_take_it_and_for_a_subagents_session()
    {
        var session = _sessions.Create("claude", "repo");
        var helper = _sessions.Helper(session, "claude", "Helper");
        _here.Refuses = true;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"hi"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
        await CallAsync(socket, 2, "send", "send", $$"""{"session":"{{helper.SessionId}}","text":"hi"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
        await CallAsync(socket, 3, "send", "send", """{"session":"nope","text":"hi"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));

        Assert.Equal([$"send {session.SessionId} hi"], _here.Asked);
    }

    [Fact]
    public async Task Send_to_a_machine_thats_down_is_unreachable()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Down = true;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"hi"}""");

        Assert.Equal("""{"t":"error","id":1,"code":"unreachable","message":"box is down","machine":"box"}""", await ReadAsync(socket));
    }

    [Fact]
    public async Task Stop_finds_the_runs_session_and_stops_it_there()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        var turn = new RowBatch();
        turn.Rows.Add(new NewRow("run-7", null, RowKind.Turn, JsonDocument.Parse("""{"state":"started"}""").RootElement.Clone(), 1));
        _hub.Publish(session.SessionId, turn);
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "stop", """{"runId":"run-7"}""");
        Assert.Equal("""{"t":"result","id":1,"result":{"ok":true}}""", await ReadAsync(socket));
        Assert.Equal(["stop run-7"], _box.Asked);

        await CallAsync(socket, 2, "send", "stop", """{"runId":"nope"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
    }

    [Fact]
    public async Task Answer_hands_the_answer_to_the_sessions_machine()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "answer", $$$"""{"session":"{{{session.SessionId}}}","ask":"ask-1","answer":{"optionId":"yes"}}""");
        Assert.Equal("""{"t":"result","id":1,"result":{"ok":true}}""", await ReadAsync(socket));
        Assert.Equal(["""answer ask-1 {"optionId":"yes"}"""], _here.Asked);

        _here.Refuses = true;
        await CallAsync(socket, 2, "send", "answer", $$$"""{"session":"{{{session.SessionId}}}","ask":"ask-1","answer":{"optionId":"yes"}}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
    }

    [Fact]
    public async Task StopTask_stops_background_work_on_the_sessions_machine()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "stopTask", $$"""{"session":"{{session.SessionId}}","task":"task-1"}""");
        Assert.Equal("""{"t":"result","id":1,"result":{"ok":true}}""", await ReadAsync(socket));
        Assert.Equal(["stop task task-1"], _here.Asked);

        _here.Refuses = true;
        await CallAsync(socket, 2, "send", "stopTask", $$"""{"session":"{{session.SessionId}}","task":"task-1"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
    }
}
