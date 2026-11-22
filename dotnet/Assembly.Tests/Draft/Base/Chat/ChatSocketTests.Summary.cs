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
        try
        {
            await holding.Task.WaitAsync(Wait);
            var commit = Task.Run(() => Row(session, RowKind.Turn, null, """{"state":"started"}"""));
            // A read that takes the hub's lock, on its own thread, still not done 100 ms after it started means the
            // commit holds the lock, waiting on the writer.
            Task<long> probe;
            do
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                probe = Task.Factory.StartNew(() =>
                {
                    started.SetResult();
                    return _hub.LastSeq(session);
                }, TaskCreationOptions.LongRunning);
                await started.Task.WaitAsync(Wait);
            }
            while (await Task.WhenAny(probe, Task.Delay(100)) == probe);
            using var socket = await OpenAsync();

            await SendAsync(socket, """{"t":"sub","id":1,"topic":"sessions"}""");
            Assert.StartsWith("""{"t":"state""", await ReadAsync(socket));
            gate.SetResult();
            await Task.WhenAll(writer, commit, probe).WaitAsync(Wait);

            Assert.Equal("running", Text(Assert.Single(await ChangesAsync(socket)), "state"));
        }
        finally
        {
            gate.TrySetResult();
        }
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

    [Fact]
    public async Task Waiting_state_since_tracks_earliest_open_ask_and_survives_restart()
    {
        var session = _sessions.Create("claude", "repo").SessionId;
        using var socket = await OpenAsync();
        await SubscribeSessionsAsync(socket);

        RowAt(session, RowKind.Turn, null, """{"state":"started"}""", 5_000);
        RowAt(session, RowKind.Ask, "q1", """{"state":"open"}""", 10_000);
        RowAt(session, RowKind.Ask, "q2", """{"state":"open"}""", 25_000);
        var opened = await ChangesAsync(socket);

        Assert.Equal(2, opened.Count);
        Assert.Equal(("running", (long?)null), (Text(opened[0], "state"), Number(opened[0], "stateSince")));
        Assert.Equal(("waiting", 10_000L), (Text(opened[1], "state"), Number(opened[1], "stateSince")));

        var restarted = SessionSummaries.Load(_rows);
        Assert.Equal(10_000L, restarted.WaitingSince(session));
        Assert.Equal(10_000L, SessionStore.Open(_db, _time, "checkouts", restarted).Item(_sessions.Get(session)!).StateSince);

        RowAt(session, RowKind.Ask, "q1", """{"state":"answered"}""", 30_000);
        var advanced = Assert.Single(await ChangesAsync(socket));
        Assert.Equal(("waiting", 25_000L), (Text(advanced, "state"), Number(advanced, "stateSince")));
        Assert.Equal(25_000L, SessionSummaries.Load(_rows).WaitingSince(session));

        RowAt(session, RowKind.Ask, "q2", """{"state":"answered"}""", 35_000);
        RowAt(session, RowKind.Turn, null, """{"state":"finished"}""", 40_000);
        var cleared = await ChangesAsync(socket);
        Assert.Equal(2, cleared.Count);
        Assert.Equal(("running", (long?)null), (Text(cleared[0], "state"), Number(cleared[0], "stateSince")));
        Assert.Equal(("idle", (long?)null), (Text(cleared[1], "state"), Number(cleared[1], "stateSince")));
    }

    private void RowAt(string session, string kind, string? item, string payload, long createdAt)
    {
        var batch = new RowBatch();
        batch.Rows.Add(new NewRow("run-1", item, kind, JsonDocument.Parse(payload).RootElement.Clone(), createdAt));
        _hub.Publish(session, batch);
    }

    private static long? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
}
