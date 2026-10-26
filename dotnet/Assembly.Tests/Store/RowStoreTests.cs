using System.Text.Json;
using Microsoft.Data.Sqlite;
using Assembly.Engine.Events;
using Assembly.Engine.Persistence;
using Assembly.Engine.Rows;
using Assembly.Engine.Store;
using Assembly.Tests.Fixtures;

namespace Assembly.Tests.Store;

public sealed class RowStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rows-{Guid.NewGuid():N}.db");
    private readonly AppDatabase _database;
    private readonly RowStore _store;

    public RowStoreTests()
    {
        _database = AppDatabase.Open(_path);
        _store = RowStore.Open(_database);
    }

    [Fact]
    public void Seqs_count_up_from_one_per_thread_with_no_gaps()
    {
        Assert.Equal([1L, 2L], _store.Commit("a", Batch("x", "y")).Select(row => row.Seq));
        Assert.Equal([3L], _store.Commit("a", Batch("z")).Select(row => row.Seq));
        Assert.Equal([1L], _store.Commit("b", Batch("w")).Select(row => row.Seq));
    }

    [Fact]
    public void Each_committed_row_carries_the_seq_before_it()
    {
        Assert.Equal([0L, 1L], _store.Commit("a", Batch("x", "y")).Select(row => row.PrevSeq));
        Assert.Equal([2L], _store.Commit("a", Batch("z")).Select(row => row.PrevSeq));
    }

    [Fact]
    public void A_finished_message_leaves_no_deltas_and_an_unfinished_one_keeps_them()
    {
        _store.Commit("a", Rows(Delta("m1", "a"), Delta("m1", "b")));
        var saved = _store.Commit("a", Rows(Message("m1"), Delta("m2", "c")));

        Assert.Equal([2L, 3L], saved.Select(row => row.PrevSeq));
        var left = _store.RowsAfter("a", 0);
        Assert.Equal([(3L, "m1", RowKind.Message), (4L, "m2", RowKind.Delta)], left.Select(row => (row.Seq, row.ItemId!, row.Kind)));
        Assert.Equal(4, _store.LastSeq("a"));
    }

    [Fact]
    public void Rows_after_a_deleted_range_start_from_the_seq_asked_for()
    {
        // The reader holds seq 2, a delta that is gone now; the server answers "rows after 2" all the same.
        _store.Commit("a", Batch("x"));
        _store.Commit("a", Rows(Delta("m", "a"), Delta("m", "b")));
        _store.Commit("a", Rows(Message("m")));
        _store.Commit("a", Batch("y"));

        Assert.Equal([(4L, 2L), (5L, 4L)], _store.RowsAfter("a", 2).Select(row => (row.Seq, row.PrevSeq!.Value)));
        Assert.Equal([(1L, 0L), (4L, 1L), (5L, 4L)], _store.RowsAfter("a", 0).Select(row => (row.Seq, row.PrevSeq!.Value)));
    }

    [Fact]
    public void Opening_deletes_finished_messages_deltas_an_older_version_kept()
    {
        SaveFinishedMessageWithDeltas(version: 1);

        using var reopenedDatabase = AppDatabase.Open(_path);
        var reopened = RowStore.Open(reopenedDatabase);
        Assert.Equal([(3L, RowKind.Message)], reopened.RowsAfter("a", 0).Select(row => (row.Seq, row.Kind)));
        Assert.Equal(RowStore.FormatVersion, reopened.StoredFormatVersion);
    }

    [Fact]
    public void Opening_a_newer_versions_database_keeps_its_rows_and_its_version()
    {
        SaveFinishedMessageWithDeltas(version: RowStore.FormatVersion + 1);

        using var reopenedDatabase = AppDatabase.Open(_path);
        var reopened = RowStore.Open(reopenedDatabase);
        Assert.Equal([RowKind.Delta, RowKind.Delta, RowKind.Message], reopened.RowsAfter("a", 0).Select(row => row.Kind));
        Assert.Equal(RowStore.FormatVersion + 1, reopened.StoredFormatVersion);
    }

    /// <summary>Two deltas and the message that finished them, as a database at <paramref name="version"/> left them.</summary>
    private void SaveFinishedMessageWithDeltas(int version)
    {
        _store.Commit("a", Rows(Delta("m", "a"), Delta("m", "b")));
        _database.Dispose();
        using var db = new SqliteConnection($"Data Source={_path};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = $$"""
            INSERT INTO rows(thread_id, seq, run_id, item_id, kind, payload, created_at) VALUES ('a', 3, 'run-1', 'm', 'message', '{"blocks":[]}', 1);
            UPDATE threads SET next_seq = 4 WHERE thread_id = 'a';
            PRAGMA user_version = {{version}};
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void A_commit_returns_exactly_what_a_reader_gets_back()
    {
        var committed = _store.Commit("a", Batch("x", "y"));
        Assert.Equal(committed.Select(Json), _store.RowsAfter("a", 0).Select(Json));
    }

    [Fact]
    public void Rows_after_returns_only_the_newer_rows()
    {
        _store.Commit("a", Batch("x", "y", "z"));
        Assert.Equal([3L], _store.RowsAfter("a", 2).Select(row => row.Seq));
        Assert.Empty(_store.RowsAfter("a", 3));
    }

    [Fact]
    public void A_detail_update_keeps_what_it_does_not_mention()
    {
        var first = new RowBatch();
        first.Rows.Add(Tool("t1", "started", "ls"));
        first.Details.Add(new ToolDetail("t1", Input: """{"command":"ls"}""", Path: "/tmp"));
        _store.Commit("a", first);
        var second = new RowBatch();
        second.Rows.Add(Tool("t1", "completed", "ls -la"));
        second.Details.Add(new ToolDetail("t1", Output: "file.txt"));
        _store.Commit("a", second);

        Assert.Equal(new StoredToolCall("ls -la", """{"command":"ls"}""", "file.txt", null, "/tmp"), _store.ToolCall("a", "t1"));
        Assert.Null(_store.ToolCall("a", "nope"));
    }

    [Fact]
    public void A_page_shows_each_item_once_in_its_newest_form_in_the_order_items_started()
    {
        CommitFixture("acp/tools.jsonl");
        var page = _store.PageBefore("t", long.MaxValue, 100);

        Assert.False(page.HasBefore);
        Assert.DoesNotContain(page.Rows, row => row.Kind == RowKind.Delta);
        Assert.Equal(page.Rows.Count, page.Rows.Select(row => (row.ItemId ?? $"#{row.Seq}", row.Kind)).Distinct().Count());
        Assert.All(page.Rows.Where(row => row.Kind == RowKind.Tool), row => Assert.Equal("completed", row.Payload.GetProperty("state").GetString()));
        Assert.Equal(page.Rows.Select(row => row.FirstSeq), page.Rows.Select(row => row.FirstSeq).Order());
        // With finished messages' deltas gone, items still come in the order they started.
        Assert.Equal(_emitted.Select(row => row.ItemId).OfType<string>().Distinct(), page.Rows.Select(row => row.ItemId).OfType<string>().Distinct());
        // The run's final turn row is its newest form; the first one showed it starting.
        Assert.Equal("finished", page.Rows.Last(row => row.Kind == RowKind.Turn).Payload.GetProperty("state").GetString());
    }

    [Fact]
    public void Pages_walk_back_without_splitting_an_item()
    {
        CommitFixture("acp/tools.jsonl");
        var whole = _store.PageBefore("t", long.MaxValue, 100);
        var walked = new List<StoredRow>();
        long before = long.MaxValue;
        while (true)
        {
            var page = _store.PageBefore("t", before, 2);
            Assert.InRange(page.Rows.Select(row => row.FirstSeq).Distinct().Count(), 1, 2);
            walked.InsertRange(0, page.Rows);
            if (!page.HasBefore)
                break;
            before = page.Before!.Value;
        }

        Assert.Equal(whole.Rows.Select(Json), walked.Select(Json));
    }

    [Fact]
    public void An_unfinished_message_comes_back_as_one_delta_with_all_its_text()
    {
        CommitEvents([new TurnStarted(0, "run-1", "go"), new TextChunk(1000, "m", "One.\n\n"), new TextChunk(1200, "m", "Two.")]);
        var message = Assert.Single(_store.PageBefore("t", long.MaxValue, 100).Rows, row => row.ItemId == "m");

        Assert.Equal(RowKind.Delta, message.Kind);
        Assert.Equal(0, message.Payload.GetProperty("from").GetInt32());
        Assert.Equal("One.\n\nTwo.", message.Payload.GetProperty("text").GetString());
        Assert.Equal(2, message.Payload.GetProperty("blocks").GetArrayLength());
    }

    [Fact]
    public void Rows_survive_closing_and_reopening_and_the_format_is_stamped()
    {
        _store.Commit("a", Batch("x"));
        _database.Dispose();
        using var reopenedDatabase = AppDatabase.Open(_path);
        var reopened = RowStore.Open(reopenedDatabase);
        Assert.Equal(RowStore.FormatVersion, reopened.StoredFormatVersion);
        Assert.Equal([2L], reopened.Commit("a", Batch("y")).Select(row => row.Seq));
    }

    [Fact]
    public void A_copied_row_keeps_its_seq_and_only_the_next_one_is_taken()
    {
        // The Mac copies a remote thread's rows as the box numbered them, so "rows after N" means the same on both.
        var box = _store.Commit("a", Batch("x", "y", "z"));
        using var macDb = new TempDatabase();
        using var macDatabase = AppDatabase.Open(macDb.Path);
        var mac = RowStore.Open(macDatabase);

        Assert.True(mac.Copy(box[0]));
        Assert.False(mac.Copy(box[0]));
        Assert.False(mac.Copy(box[2]));
        Assert.True(mac.Copy(box[1]));
        Assert.True(mac.Copy(box[2]));
        Assert.Equal(box.Select(Json), mac.RowsAfter("a", 0).Select(Json));
        Assert.Equal(3, mac.LastSeq("a"));
        Assert.Equal(0, mac.LastSeq("b"));
    }

    [Fact]
    public void A_copy_takes_a_row_across_a_gap_and_a_copied_message_deletes_its_deltas()
    {
        using var macDb = new TempDatabase();
        using var macDatabase = AppDatabase.Open(macDb.Path);
        var mac = RowStore.Open(macDatabase);
        // Live, the Mac got the deltas before the message finished.
        var x = _store.Commit("a", Batch("x"));
        var deltas = _store.Commit("a", Rows(Delta("m", "a"), Delta("m", "b")));
        Assert.All(x.Concat(deltas), row => Assert.True(mac.Copy(row)));
        var message = _store.Commit("a", Rows(Message("m")));
        Assert.True(mac.Copy(message[0]));
        Assert.Equal(_store.RowsAfter("a", 0).Select(Json), mac.RowsAfter("a", 0).Select(Json));

        // A Mac that joins later gets the box's rows with the gap, and takes them as they are.
        using var lateDb = new TempDatabase();
        using var lateDatabase = AppDatabase.Open(lateDb.Path);
        var late = RowStore.Open(lateDatabase);
        var caughtUp = _store.RowsAfter("a", 0);
        Assert.False(late.Copy(caughtUp[1]));
        Assert.All(caughtUp, row => Assert.True(late.Copy(row)));
        Assert.Equal(4, late.LastSeq("a"));
        Assert.Equal(caughtUp.Select(Json), late.RowsAfter("a", 0).Select(Json));
    }

    [Fact]
    public void A_copied_tool_detail_opens_like_a_committed_one()
    {
        var batch = new RowBatch();
        batch.Rows.Add(Tool("t1", "completed", "ls"));
        var box = _store.Commit("a", batch);
        using var macDb = new TempDatabase();
        using var macDatabase = AppDatabase.Open(macDb.Path);
        var mac = RowStore.Open(macDatabase);
        mac.Copy(box[0]);

        Assert.Null(mac.ToolCall("a", "t1"));
        mac.SaveToolDetail("a", new ToolDetail("t1", Input: """{"command":"ls"}""", Output: "a.txt"));
        Assert.Equal(new StoredToolCall("ls", """{"command":"ls"}""", "a.txt", null, null), mac.ToolCall("a", "t1"));
    }

    [Fact]
    public void A_threads_open_run_and_a_runs_thread_come_from_the_turn_rows()
    {
        _store.Commit("a", Turn("run-1", "started"));
        Assert.Equal("run-1", _store.OpenRun("a"));
        Assert.False(_store.RunEnded("a", "run-1"));
        Assert.Equal("a", _store.ThreadOfRun("run-1"));

        _store.Commit("a", Turn("run-1", "finished"));
        Assert.Null(_store.OpenRun("a"));
        Assert.True(_store.RunEnded("a", "run-1"));
        Assert.Null(_store.OpenRun("b"));
        Assert.Null(_store.ThreadOfRun("nope"));
    }

    private void CommitFixture(string name) => CommitEvents(FixtureReplay.Events(name));

    private readonly List<NewRow> _emitted = [];

    private void CommitEvents(IEnumerable<AgentEvent> events)
    {
        var converter = new RowConverter();
        foreach (var agentEvent in events)
        {
            var batch = converter.Apply(agentEvent);
            _emitted.AddRange(batch.Rows);
            _store.Commit("t", batch);
        }
    }

    private static RowBatch Rows(params NewRow[] rows)
    {
        var batch = new RowBatch();
        batch.Rows.AddRange(rows);
        return batch;
    }

    private static NewRow Delta(string itemId, string text) =>
        new("run-1", itemId, RowKind.Delta, Payload($$"""{"from":0,"blocks":[],"text":"{{text}}"}"""), 1);

    private static NewRow Message(string itemId) => new("run-1", itemId, RowKind.Message, Payload("""{"blocks":[]}"""), 1);

    private static RowBatch Batch(params string[] texts)
    {
        var batch = new RowBatch();
        foreach (var text in texts)
            batch.Rows.Add(new NewRow("run-1", null, RowKind.User, Payload($$"""{"text":"{{text}}"}"""), 1));
        return batch;
    }

    private static NewRow Tool(string id, string state, string summary) =>
        new("run-1", id, RowKind.Tool, Payload($$"""{"name":"Bash","state":"{{state}}","summary":"{{summary}}","hasDetail":true}"""), 1);

    private static RowBatch Turn(string runId, string state)
    {
        var batch = new RowBatch();
        batch.Rows.Add(new NewRow(runId, null, RowKind.Turn, Payload($$"""{"state":"{{state}}"}"""), 1));
        return batch;
    }

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Json(StoredRow row) =>
        $"{row.ThreadId}|{row.Seq}|{row.RunId}|{row.ItemId}|{row.Kind}|{row.CreatedAt}|{row.FirstSeq}|{row.PrevSeq}|{row.Payload.GetRawText()}";

    public void Dispose()
    {
        _database.Dispose();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(file);
    }
}
