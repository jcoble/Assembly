using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Draft.Base.Chat;

// Step 1c: each session's item carries its summary, which a row changes only when it changes a summary field.
public sealed partial class ChatSocketTests
{
    [Fact]
    public async Task A_thousand_row_turn_sends_two_upserts()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        using var socket = await OpenAsync();
        await SubscribeSessionsAsync(socket);

        Row(session, RowKind.Turn, null, """{"state":"started"}""");
        for (var i = 0; i < 1000; i++)
            Row(session, RowKind.Message, $"m{i}", """{"text":"x"}""");
        Row(session, RowKind.Turn, null, """{"state":"finished"}""");
        var changes = await ChangesAsync(socket);

        Assert.Equal(2, changes.Count);
        Assert.Equal(("running", "run-1", false), (Text(changes[0], "state"), Text(changes[0], "activeRunId"), Flag(changes[0], "unread")));
        Assert.Equal(("idle", null, true, true), (Text(changes[1], "state"), Text(changes[1], "activeRunId"), Flag(changes[1], "unread"),
            Flag(changes[1], "needsAttention")));
    }

    [Fact]
    public async Task The_summary_follows_asks_background_work_and_a_failed_turn()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        using var socket = await OpenAsync();
        await SubscribeSessionsAsync(socket);

        Row(session, RowKind.Turn, null, """{"state":"started"}""");
        Row(session, RowKind.Ask, "a1", """{"kind":"permission","state":"open"}""");
        Row(session, RowKind.Ask, "a1", """{"kind":"permission","state":"answered"}""");
        Row(session, RowKind.Task, "t1", """{"name":"build","state":"running","canStop":true}""");
        Row(session, RowKind.Subagent, "s1", """{"sessionId":"child","title":"look","state":"requires_action","canStop":true}""");
        Row(session, RowKind.Turn, null, """{"state":"failed"}""");
        Row(session, RowKind.Task, "t1", """{"name":"build","state":"completed","canStop":false}""");
        Row(session, RowKind.Subagent, "s1", """{"sessionId":"child","title":"look","state":"idle","canStop":false}""");
        var changes = await ChangesAsync(socket);

        Assert.Equal(
        [
            ("running", false, 0, false), ("waiting", true, 0, true), ("running", false, 0, false), ("running", false, 1, false),
            ("running", false, 2, false), ("failed", false, 2, true), ("failed", false, 1, true), ("failed", false, 0, true),
        ], changes.Select(item => (Text(item, "state"), Flag(item, "pendingAsk"), item.GetProperty("background").GetInt32(), Flag(item, "needsAttention"))));
    }

    [Fact]
    public async Task Only_a_turns_end_sets_unread_and_sessions_read_clears_it()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        using var socket = await OpenAsync();
        await SubscribeSessionsAsync(socket);

        Row(session, RowKind.User, null, """{"text":"hi"}""");
        Row(session, RowKind.Delta, "m1", """{"text":"h"}""");
        Assert.Empty(await ChangesAsync(socket));
        Row(session, RowKind.Turn, null, """{"state":"started"}""");
        Row(session, RowKind.Turn, null, """{"state":"finished"}""");
        Assert.True(Flag((await ChangesAsync(socket))[^1], "unread"));

        // Read up to before the turn's end, it's still unread; up to its end, it's read; a mark below that changes nothing.
        var end = _hub.LastSeq(session);
        await CallAsync(socket, 2, "sessions", "read", $$"""{"session":"{{session}}","seq":{{end - 1}}}""");
        Assert.True(Flag(Assert.Single(await ChangesAsync(socket, 2)), "unread"));
        await CallAsync(socket, 3, "sessions", "read", $$"""{"session":"{{session}}","seq":{{end}}}""");
        Assert.False(Flag(Assert.Single(await ChangesAsync(socket, 3)), "unread"));
        await CallAsync(socket, 4, "sessions", "read", $$"""{"session":"{{session}}","seq":{{end - 1}}}""");
        Assert.Empty(await ChangesAsync(socket, 4));
        await CallAsync(socket, 5, "sessions", "read", """{"session":"nope","seq":1}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
    }

    // The state comes from the summaries in memory: with the rows table gone, any query of it would throw.
    [Fact]
    public async Task The_sessions_state_reads_summaries_without_a_row_query()
    {
        var sessions = Enumerable.Range(0, SessionsFeature.ListLimit).Select(i =>
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            return _sessions.Create("claude", $"repo-{i}").SessionId;
        }).ToList();
        Row(sessions[^1], RowKind.Turn, null, """{"state":"started"}""");
        Row(sessions[^2], RowKind.Ask, "a1", """{"kind":"question","state":"open"}""");
        _db.Writer.Write("test.no-rows", scope => scope.Execute("ALTER TABLE rows RENAME TO rows_gone;"));
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"sessions"}""");
        using var state = JsonDocument.Parse(await ReadAsync(socket));

        var items = state.RootElement.GetProperty("items").EnumerateArray().ToDictionary(entry => entry.GetProperty("key").GetString()!, entry => entry.GetProperty("item"));
        Assert.Equal(SessionsFeature.ListLimit, items.Count);
        Assert.Equal("running", Text(items[sessions[^1]], "state"));
        Assert.Equal("waiting", Text(items[sessions[^2]], "state"));
        Assert.Equal("idle", Text(items[sessions[0]], "state"));
    }

    // A row's commit waits for the writer while holding the hub's lock, then publishes; a sessions sub reading its state
    // meanwhile must not need the hub's lock, or the two wait on each other once the commit goes on.
    [Fact]
    public async Task A_sessions_sub_reads_its_state_while_a_commit_holds_the_hub()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(() => _db.Writer.Write("test.hold", _ =>
        {
            holding.SetResult();
            gate.Task.Wait();
            return 0;
        }));
        await holding.Task.WaitAsync(Wait);
        var commit = Task.Run(() => Row(session, RowKind.Turn, null, """{"state":"started"}"""));
        // Nothing marks the moment the commit holds the hub and waits on the writer; give it that long.
        await Task.Delay(200);
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"sessions"}""");
        Assert.StartsWith("""{"t":"state""", await ReadAsync(socket));
        gate.SetResult();
        await Task.WhenAll(writer, commit).WaitAsync(Wait);

        Assert.Equal("running", Text(Assert.Single(await ChangesAsync(socket)), "state"));
    }

    // One row in its own batch, as a turn streams them, under run-1.
    private void Row(string session, string kind, string? item, string payload)
    {
        var batch = new RowBatch();
        batch.Rows.Add(new NewRow("run-1", item, kind, JsonDocument.Parse(payload).RootElement.Clone(), 1));
        _hub.Publish(session, batch);
    }

    private static async Task SubscribeSessionsAsync(WebSocket socket)
    {
        await SendAsync(socket, """{"t":"sub","id":1,"topic":"sessions"}""");
        Assert.StartsWith("""{"t":"state""", await ReadAsync(socket));
    }

    // The sessions changes sent before a probe call's answer: everything published so far. With an id, that call's
    // answer is the probe instead.
    private async Task<List<JsonElement>> ChangesAsync(WebSocket socket, long? id = null)
    {
        if (id is null)
        {
            id = 99;
            await CallAsync(socket, 99, "preferences", "set", Save);
        }
        var changes = new List<JsonElement>();
        while (true)
        {
            using var frame = JsonDocument.Parse(await ReadAsync(socket));
            var root = frame.RootElement;
            if (root.GetProperty("t").GetString() is "result" or "error" && root.GetProperty("id").GetInt64() == id.Value)
                return changes;
            if (root.GetProperty("t").GetString() == "change")
                changes.Add(root.GetProperty("item").Clone());
        }
    }

    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static bool Flag(JsonElement item, string name) => item.GetProperty(name).GetBoolean();
}
