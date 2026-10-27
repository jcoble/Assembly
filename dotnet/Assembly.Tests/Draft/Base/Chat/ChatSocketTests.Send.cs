using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Tests.Features.Chat.Sessions;

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

    [Fact]
    public async Task Stop_answer_and_stopTask_on_a_machine_thats_down_are_unreachable()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        var turn = new RowBatch();
        turn.Rows.Add(new NewRow("run-7", null, RowKind.Turn, JsonDocument.Parse("""{"state":"started"}""").RootElement.Clone(), 1));
        _hub.Publish(session.SessionId, turn);
        _box.Down = true;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "stop", """{"runId":"run-7"}""");
        Assert.Equal("""{"t":"error","id":1,"code":"unreachable","message":"box is down","machine":"box"}""", await ReadAsync(socket));
        await CallAsync(socket, 2, "send", "answer", $$$"""{"session":"{{{session.SessionId}}}","ask":"ask-1","answer":{"optionId":"yes"}}""");
        Assert.Equal("""{"t":"error","id":2,"code":"unreachable","message":"box is down","machine":"box"}""", await ReadAsync(socket));
        await CallAsync(socket, 3, "send", "stopTask", $$"""{"session":"{{session.SessionId}}","task":"task-1"}""");
        Assert.Equal("""{"t":"error","id":3,"code":"unreachable","message":"box is down","machine":"box"}""", await ReadAsync(socket));
    }

    private const string Injected = """{"outcome":"injected"}""";
    private const string BadClientId = "clientId must be 1 to 64 letters, digits, - or _";

    [Fact]
    public async Task Send_refuses_a_clientId_that_isnt_1_to_64_letters_digits_dashes_or_underscores()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();

        string[] bad = ["", new string('a', 65), "a b", "a/b"];
        for (var id = 1; id <= bad.Length; id++)
        {
            await CallAsync(socket, id, "send", "send", Args(session.SessionId, "hi", bad[id - 1]));
            Assert.Equal($$"""{"t":"error","id":{{id}},"code":"failed","message":"{{BadClientId}}"}""", await ReadAsync(socket));
        }
        Assert.Empty(_here.Asked);

        await CallAsync(socket, 9, "send", "send", Args(session.SessionId, "hi", new string('a', 64)));
        Assert.Equal(Result(9, "run-1"), await ReadAsync(socket));
    }

    [Fact]
    public async Task A_retried_steer_answers_the_first_ones_run_and_the_agent_is_steered_once()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenRealAsync();
        var (runId, agent, prompt) = await RunningTurnAsync(socket, session.SessionId);

        await CallAsync(socket, 2, "send", "send", Args(session.SessionId, "also this", "c3"));
        await agent.ReplyAsync(await agent.ReadAsync("_session/steering"), Injected);
        Assert.Equal(Result(2, runId), await ReadAsync(socket));
        await CallAsync(socket, 3, "send", "send", Args(session.SessionId, "also this", "c3"));
        Assert.Equal(Result(3, runId), await ReadAsync(socket));

        // A reconnected window resends while the agent still holds the first: the resend gets the first's answer.
        await CallAsync(socket, 4, "send", "send", Args(session.SessionId, "and this", "c4"));
        var held = await agent.ReadAsync("_session/steering");
        using var reconnected = await OpenRealAsync();
        await CallAsync(reconnected, 1, "send", "send", Args(session.SessionId, "and this", "c4"));
        await _log.WrittenAsync("send c4: joined in-flight send");
        await agent.ReplyAsync(held, Injected);
        Assert.Equal(Result(4, runId), await ReadAsync(socket));
        Assert.Equal(Result(1, runId), await ReadAsync(reconnected));

        // The next thing the agent hears is the turn's close: no second steer.
        await EndTurnAsync(agent, prompt);
        Assert.Equal((1, 1), (UserRows(session.SessionId, "c3"), UserRows(session.SessionId, "c4")));
    }

    [Fact]
    public async Task A_send_resent_on_a_second_socket_at_the_same_time_starts_one_turn()
    {
        var session = _sessions.Create("claude", "repo");
        using var first = await OpenRealAsync();
        using var second = await OpenRealAsync();

        await CallAsync(first, 1, "send", "send", Args(session.SessionId, "hi", "c13"));
        await CallAsync(second, 1, "send", "send", Args(session.SessionId, "hi", "c13"));
        var answers = new[] { await ReadAsync(first), await ReadAsync(second) };

        Assert.All(answers, answer => Assert.StartsWith("""{"t":"result","id":1,""", answer));
        Assert.Equal(answers[0], answers[1]);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await EndTurnAsync(agent, await agent.ReadAsync("session/prompt"));
        Assert.Equal(1, UserRows(session.SessionId, "c13"));
        Assert.Single(_rows.RowsAfter(session.SessionId, 0), row => row.Kind == RowKind.Turn && row.Payload.GetProperty("state").GetString() == "started");
    }

    [Fact]
    public async Task A_retry_waits_on_its_own_token_and_answers_the_first_ones_failure_when_it_is_cut_off()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenRealAsync();
        using var retry = await OpenRealAsync();
        var (runId, agent, prompt) = await RunningTurnAsync(socket, session.SessionId);

        // Cancelling the retry leaves the first send alone.
        await CallAsync(socket, 2, "send", "send", Args(session.SessionId, "also this", "c7"));
        var held = await agent.ReadAsync("_session/steering");
        await CallAsync(retry, 1, "send", "send", Args(session.SessionId, "also this", "c7"));
        await _log.WrittenAsync("send c7: joined in-flight send");
        await SendAsync(retry, """{"t":"cancel","id":1}""");
        Assert.Contains("\"code\":\"cancelled\"", await ReadAsync(retry));
        await agent.ReplyAsync(held, Injected);
        Assert.Equal(Result(2, runId), await ReadAsync(socket));

        // The first is cut off by its 30 s limit after its request went out, while the retry waits on it: with no row,
        // the retry fails, saying so, and sends nothing.
        await CallAsync(socket, 3, "send", "send", Args(session.SessionId, "and this", "c8"));
        await agent.ReadAsync("_session/steering");
        _time.Advance(TimeSpan.FromSeconds(20));
        await CallAsync(retry, 2, "send", "send", Args(session.SessionId, "and this", "c8"));
        await _log.WrittenAsync("send c8: joined in-flight send");
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Contains("\"code\":\"timeout\"", await ReadAsync(socket));
        Assert.Equal("""{"t":"error","id":2,"code":"failed","message":"this message's first send was cut off; it may have reached the agent"}""",
            await ReadAsync(retry));
        Assert.DoesNotContain(_calls.Entries, entry => entry.Level >= LogLevel.Error);

        // The next thing the agent hears is the turn's close: c8 was a steering request once.
        await EndTurnAsync(agent, prompt);
        Assert.Equal((1, 0), (UserRows(session.SessionId, "c7"), UserRows(session.SessionId, "c8")));
    }

    [Fact]
    public async Task A_steer_the_agent_takes_just_after_its_turn_ended_lands_on_that_turn_once()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenRealAsync();
        var (runId, agent, prompt) = await RunningTurnAsync(socket, session.SessionId);

        await CallAsync(socket, 2, "send", "send", Args(session.SessionId, "also this", "c14"));
        var steer = await agent.ReadAsync("_session/steering");
        // The turn ends, and only then does the agent say it took the message.
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        var close = await agent.ReadAsync("session/close");
        await agent.ReplyAsync(steer, Injected);
        Assert.Equal(Result(2, runId), await ReadAsync(socket));
        await agent.ReplyAsync(close, "{}");

        await CallAsync(socket, 3, "send", "send", Args(session.SessionId, "also this", "c14"));
        Assert.Equal(Result(3, runId), await ReadAsync(socket));
        Assert.Equal(runId, Assert.Single(_rows.RowsAfter(session.SessionId, 0), row => ClientId(row) == "c14").RunId);

        // The next thing the agent hears is the next message's turn: c14 was a steering request once, and never sent again.
        await CallAsync(socket, 4, "send", "send", Args(session.SessionId, "next", "c15"));
        var next = RunOf(await ReadAsync(socket));
        await agent.OpenAsync();
        var second = await agent.ReadAsync("session/prompt");
        Assert.Equal("next", second.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await EndTurnAsync(agent, second);
        Assert.NotEqual(runId, next);
    }

    [Fact]
    public async Task A_steer_the_agent_wants_as_a_new_prompt_stops_waiting_at_its_limit_and_otherwise_starts_the_next_turn()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenRealAsync();
        var (runId, agent, prompt) = await RunningTurnAsync(socket, session.SessionId);
        using var live = _hub.Subscribe(session.SessionId, after: _rows.LastSeq(session.SessionId));

        // The agent wants the message as its own prompt, but its turn goes on: the call ends at its limit.
        await CallAsync(socket, 2, "send", "send", Args(session.SessionId, "two", "c9"));
        await agent.ReplyThenTextAsync(await agent.ReadAsync("_session/steering"), """{"outcome":"promptRequired","reason":"noRunningTurn"}""", "Still going", "m1");
        await live.Rows.ReadAsync().AsTask().WaitAsync(Wait);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Contains("\"code\":\"timeout\"", await ReadAsync(socket));

        // When the turn ends, the message starts the next one; its row carries the clientId, and a retry answers that run.
        await CallAsync(socket, 3, "send", "send", Args(session.SessionId, "three", "c10"));
        await agent.ReplyAsync(await agent.ReadAsync("_session/steering"), """{"outcome":"promptRequired","reason":"noRunningTurn"}""");
        await EndTurnAsync(agent, prompt);
        var next = RunOf(await ReadAsync(socket));
        Assert.NotEqual(runId, next);
        await agent.OpenAsync();
        var second = await agent.ReadAsync("session/prompt");
        await CallAsync(socket, 4, "send", "send", Args(session.SessionId, "three", "c10"));
        Assert.Equal(Result(4, next), await ReadAsync(socket));
        await EndTurnAsync(agent, second);
        Assert.Equal(next, Assert.Single(_rows.RowsAfter(session.SessionId, 0), row => ClientId(row) == "c10").RunId);
    }

    [Fact]
    public async Task A_clientId_sent_again_with_other_text_fails_and_logs_one_warning()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenRealAsync();

        await CallAsync(socket, 1, "send", "send", Args(session.SessionId, "hi", "c11"));
        var runId = RunOf(await ReadAsync(socket));
        await CallAsync(socket, 2, "send", "send", Args(session.SessionId, "bye", "c11"));
        Assert.Equal("""{"t":"error","id":2,"code":"failed","message":"clientId already used for another message"}""", await ReadAsync(socket));
        await CallAsync(socket, 3, "send", "send", Args(session.SessionId, "hi", "c11"));
        Assert.Equal(Result(3, runId), await ReadAsync(socket));

        Assert.Single(_log.ToString().Split('\n'), line => line.Contains("Warning", StringComparison.Ordinal));
        Assert.Equal(1, UserRows(session.SessionId, "c11"));
    }

    [Fact]
    public async Task The_same_clientId_in_another_session_starts_that_sessions_own_turn()
    {
        var one = _sessions.Create("claude", "repo");
        var other = _sessions.Create("claude", "repo");
        using var socket = await OpenRealAsync();

        await CallAsync(socket, 1, "send", "send", Args(one.SessionId, "hi", "c12"));
        var first = RunOf(await ReadAsync(socket));
        await CallAsync(socket, 2, "send", "send", Args(other.SessionId, "hi", "c12"));
        var second = RunOf(await ReadAsync(socket));

        Assert.NotEqual(first, second);
        Assert.Equal((1, 1), (UserRows(one.SessionId, "c12"), UserRows(other.SessionId, "c12")));
    }

    // A turn running on the fake agent, started over the socket as call 1, with no clientId.
    private async Task<(string RunId, FakeLink Agent, JsonElement Prompt)> RunningTurnAsync(WebSocket socket, string sessionId)
    {
        await CallAsync(socket, 1, "send", "send", $$"""{"session":"{{sessionId}}","text":"one"}""");
        var runId = RunOf(await ReadAsync(socket));
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        return (runId, agent, await agent.ReadAsync("session/prompt"));
    }

    // Ends the turn, and checks the next thing the agent hears is its session's close.
    private static async Task EndTurnAsync(FakeLink agent, JsonElement prompt)
    {
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    private static string Args(string sessionId, string text, string clientId) =>
        JsonSerializer.Serialize(new { session = sessionId, text, clientId });

    private static string Result(long id, string runId) => $$$"""{"t":"result","id":{{{id}}},"result":{"runId":"{{{runId}}}"}}""";

    private static string RunOf(string answer) => JsonDocument.Parse(answer).RootElement.GetProperty("result").GetProperty("runId").GetString()!;

    private static string? ClientId(StoredRow row) =>
        row.Kind == RowKind.User && row.Payload.TryGetProperty("clientId", out var id) ? id.GetString() : null;

    private int UserRows(string sessionId, string clientId) => _rows.RowsAfter(sessionId, 0).Count(row => ClientId(row) == clientId);
}
