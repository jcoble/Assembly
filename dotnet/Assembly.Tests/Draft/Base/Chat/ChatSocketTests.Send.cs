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
    public async Task Send_commits_a_queued_owner_message_on_the_sessions_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"hi"}""");

        Accepted(await ReadAsync(socket), session.SessionId);
        Assert.Equal([$"owner prompt {session.SessionId} hi"], _box.Asked);
        Assert.Empty(_here.Asked);
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
        Assert.Contains("\"code\":\"unreachable\"", await ReadAsync(socket));
    }

    // A cancel that lands once the machine has started the turn can't stop it, so the send answers its run.
    [Fact]
    public async Task A_cancel_after_owner_preparation_still_answers_the_committed_receipt()
    {
        var session = ReadyParent();
        _here.Started = true;
        _here.Hold();
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"hi"}""");
        await _here.Reached.Task.WaitAsync(Wait);
        await SendAsync(socket, """{"t":"cancel","id":1}""");
        await _here.Cancelled.Task.WaitAsync(Wait);
        _here.Release();

        Accepted(await ReadAsync(socket), session.SessionId);
    }

    // The same for a stop the machine has already carried out.
    [Fact]
    public async Task A_cancel_after_the_run_stopped_still_answers_ok()
    {
        var session = ReadyParent();
        var turn = new RowBatch();
        turn.Rows.Add(new NewRow("run-7", null, RowKind.Turn, JsonDocument.Parse("""{"state":"started"}""").RootElement.Clone(), 1));
        _hub.Publish(session.SessionId, turn);
        _here.Started = true;
        _here.Hold();
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "stop", """{"runId":"run-7"}""");
        await _here.Reached.Task.WaitAsync(Wait);
        await SendAsync(socket, """{"t":"cancel","id":1}""");
        await _here.Cancelled.Task.WaitAsync(Wait);
        _here.Release();

        Assert.Equal("""{"t":"result","id":1,"result":{"ok":true}}""", await ReadAsync(socket));
    }

    [Fact]
    public async Task A_retried_owner_prompt_replays_its_receipt_and_the_agent_is_steered_once()
    {
        var session = ReadyParent();
        using var socket = await OpenRealAsync();
        var (_, agent, prompt) = await RunningTurnAsync(socket, session.SessionId);
        await CallAsync(socket, 2, "send", "send", Args(session.SessionId, "also this", "c3"));
        var first = Accepted(await ReadAsync(socket), session.SessionId, "c3");
        await CallAsync(socket, 3, "send", "send", Args(session.SessionId, "also this", "c3"));
        Assert.Equal(first, Accepted(await ReadAsync(socket), session.SessionId, "c3"));
        Assert.Equal(1, PromptCount(session.SessionId, "c3"));
        await agent.ReplyAsync(await agent.ReadAsync("_session/steering"), """{"outcome":"injected"}""");
        await Delivered(session.SessionId, "c3");
        Assert.Equal(1, UserRows(session.SessionId, "c3"));
        await EndTurnAsync(agent, prompt);
    }

    [Fact]
    public async Task A_send_resent_on_a_second_socket_at_the_same_time_starts_one_turn()
    {
        var session = ReadyParent();
        using var first = await OpenRealAsync();
        using var second = await OpenRealAsync();
        await CallAsync(first, 1, "send", "send", Args(session.SessionId, "hi", "c13"));
        await CallAsync(second, 1, "send", "send", Args(session.SessionId, "hi", "c13"));
        var answers = new[] { await ReadAsync(first), await ReadAsync(second) };
        Accepted(answers[0], session.SessionId, "c13");
        Assert.Equal(answers[0], answers[1]);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "provider-13");
        await Delivered(session.SessionId, "c13");
        Assert.Equal(1, UserRows(session.SessionId, "c13"));
        Assert.Single(_rows.RowsAfter(session.SessionId, 0), row => row.Kind == RowKind.Turn && row.Payload.GetProperty("state").GetString() == "started");
        await EndTurnAsync(agent, prompt);
    }

    [Fact]
    public async Task A_retry_and_cancel_do_not_cancel_an_accepted_owner_delivery()
    {
        var session = ReadyParent();
        using var socket = await OpenRealAsync();
        using var retry = await OpenRealAsync();
        await CallAsync(socket, 1, "send", "send", Args(session.SessionId, "one", "c7"));
        var original = Accepted(await ReadAsync(socket), session.SessionId, "c7");
        var agent = await _launcher.NextAsync(); // initialize/prompt has not completed
        await CallAsync(retry, 1, "send", "send", Args(session.SessionId, "one", "c7"));
        Assert.Equal(original, Accepted(await ReadAsync(retry), session.SessionId, "c7"));
        await SendAsync(retry, """{"t":"cancel","id":1}""");
        _time.Advance(TimeSpan.FromSeconds(30));
        await CallAsync(socket, 2, "send", "send", Args(session.SessionId, "one", "c7"));
        Assert.Equal(original, Accepted(await ReadAsync(socket), session.SessionId, "c7"));
        Assert.Equal(1, PromptCount(session.SessionId, "c7"));
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "provider-7");
        await Delivered(session.SessionId, "c7");
        Assert.Equal(1, UserRows(session.SessionId, "c7"));
        await EndTurnAsync(agent, prompt);
    }

    [Fact]
    public async Task An_accepted_steer_after_turn_end_keeps_its_original_receipt_and_single_row()
    {
        var session = ReadyParent();
        using var socket = await OpenRealAsync();
        var (_, agent, prompt) = await RunningTurnAsync(socket, session.SessionId);
        await CallAsync(socket, 2, "send", "send", Args(session.SessionId, "also this", "c14"));
        var accepted = Accepted(await ReadAsync(socket), session.SessionId, "c14");
        var steering = await agent.ReadAsync("_session/steering");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        // The session is kept while the steer is registered, so the agent's reply comes before the close.
        await agent.ReplyAsync(steering, """{"outcome":"injected"}""");
        await Delivered(session.SessionId, "c14");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"running"}""");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        await agent.ReplyAsync(await agent.ReadAsync("session/close"), "{}");
        await CallAsync(socket, 3, "send", "send", Args(session.SessionId, "also this", "c14"));
        Assert.Equal(accepted, Accepted(await ReadAsync(socket), session.SessionId, "c14"));
        Assert.Equal(1, UserRows(session.SessionId, "c14"));
    }

    [Fact]
    public async Task An_owner_prompt_requiring_the_next_turn_keeps_its_receipt_beyond_the_call_deadline()
    {
        var session = ReadyParent();
        using var socket = await OpenRealAsync();
        var (_, agent, prompt) = await RunningTurnAsync(socket, session.SessionId);
        await CallAsync(socket, 2, "send", "send", Args(session.SessionId, "two", "c9"));
        var accepted = Accepted(await ReadAsync(socket), session.SessionId, "c9");
        await agent.ReplyAsync(await agent.ReadAsync("_session/steering"), """{"outcome":"promptRequired","reason":"noRunningTurn"}""");
        _time.Advance(TimeSpan.FromSeconds(30));
        await CallAsync(socket, 3, "send", "send", Args(session.SessionId, "two", "c9"));
        Assert.Equal(accepted, Accepted(await ReadAsync(socket), session.SessionId, "c9"));
        Assert.Equal(0, UserRows(session.SessionId, "c9"));
        await EndTurnAsync(agent, prompt);
        await agent.OpenAsync();
        var next = await agent.ReadAsync("session/prompt");
        Assert.Equal("two", next.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await agent.InsertAsync(next, "provider-9");
        await Delivered(session.SessionId, "c9");
        Assert.Equal(1, UserRows(session.SessionId, "c9"));
        await EndTurnAsync(agent, next);
    }

    [Fact]
    public async Task A_clientId_conflict_preserves_the_original_owner_message_and_receipt()
    {
        var session = ReadyParent();
        using var socket = await OpenRealAsync();
        await CallAsync(socket, 1, "send", "send", Args(session.SessionId, "hi", "c11"));
        var original = Accepted(await ReadAsync(socket), session.SessionId, "c11");
        await CallAsync(socket, 2, "send", "send", Args(session.SessionId, "bye", "c11"));
        Assert.Equal("""{"t":"error","id":2,"code":"failed","message":"client_id_conflict"}""", await ReadAsync(socket));
        await CallAsync(socket, 3, "send", "send", Args(session.SessionId, "hi", "c11"));
        Assert.Equal(original, Accepted(await ReadAsync(socket), session.SessionId, "c11"));
        Assert.Equal(1, PromptCount(session.SessionId, "c11"));
        Assert.Equal("hi", _db.QueryFirst("SELECT text FROM engine_message WHERE client_id='c11'", r => r.Text(0), default));
    }

    // A turn running on the fake agent, started over the socket as call 1, with no clientId.
    private async Task<(string RunId, FakeLink Agent, JsonElement Prompt)> RunningTurnAsync(WebSocket socket, string sessionId)
    {
        await CallAsync(socket, 1, "send", "send", $$"""{"session":"{{sessionId}}","text":"one"}""");
        Accepted(await ReadAsync(socket), sessionId);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "first-provider");
        await Delivered(sessionId);
        var runId = _rows.RowsAfter(sessionId, 0).Single(row => row.Kind == RowKind.User).RunId!;
        return (runId, agent, prompt);
    }

    // Ends the turn, and checks the next thing the agent hears is its session's close.
    private static async Task EndTurnAsync(FakeLink agent, JsonElement prompt)
    {
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    private static string Args(string sessionId, string text, string clientId) =>
        JsonSerializer.Serialize(new { session = sessionId, text, clientId });

    private static string Accepted(string frame, string session, string? client = null)
    {
        var ack = Result(frame);
        Assert.Equal(session, ack.GetProperty("sessionId").GetString());
        if (client is not null) Assert.Equal(client, ack.GetProperty("clientId").GetString());
        Assert.Equal("prompt/" + session + "/" + ack.GetProperty("clientId").GetString(), ack.GetProperty("messageId").GetString());
        Assert.Equal("queued", ack.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, ack.GetProperty("runId").ValueKind);
        Assert.True(ack.GetProperty("messageVersion").GetInt64() > 0);
        Assert.True(ack.GetProperty("seq").GetInt64() > 0);
        return ack.GetRawText();
    }

    private int PromptCount(string session, string client) => (int)_db.QueryFirst(
        "SELECT COUNT(*) FROM engine_message WHERE to_session_id=$session AND client_id=$client", r => r.Long(0), default,
        new Assembly.Application.Common.Database.SqlArg("$session", session), new Assembly.Application.Common.Database.SqlArg("$client", client));

    private async Task Delivered(string session, string? client = null)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var changed = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        using var subscription = _engine.Publisher.Register(_ => changed.Writer.TryWrite(true), out _);
        while (_db.QueryFirst("SELECT COUNT(*) FROM engine_message WHERE to_session_id=$session AND ($client IS NULL OR client_id=$client) AND state IN ('next_turn','delivered_to_turn')",
            r => r.Long(0), default, new Assembly.Application.Common.Database.SqlArg("$session", session), new Assembly.Application.Common.Database.SqlArg("$client", client)) == 0)
            await changed.Reader.ReadAsync(timeout.Token);
    }

    private static string? ClientId(StoredRow row) =>
        row.Kind == RowKind.User && row.Payload.TryGetProperty("clientId", out var id) ? id.GetString() : null;

    private int UserRows(string sessionId, string clientId) => _rows.RowsAfter(sessionId, 0).Count(row => ClientId(row) == clientId);
}
