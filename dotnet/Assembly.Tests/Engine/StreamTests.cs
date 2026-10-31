using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Reading;

namespace Assembly.Tests.Engine;

public sealed class StreamTests
{
    private static void Event(EngineFixture f, string kind, string id, EngineEventData? data = null) =>
        f.Db.Writer.Write("event", s => EngineJournal.Commit(s,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "window"), id, 0, 0, 1000),
            0, 0, [new("changed", kind, id, 0, false, data ?? new(null, null, null, null, null, null))], []));

    [Fact]
    public void Filtering_advances_global_watermark_without_false_gap()
    {
        using var f = new EngineFixture();
        Event(f, "session", "a"); Event(f, "usage", "u"); Event(f, "session", "b");
        var reader = new EngineReader(f.Db, f.Machine);
        var page = reader.Since("usage", null, 0, default);
        Assert.Equal(new long[] { 2 }, page.Events.Select(e => e.Seq));
        Assert.Equal(3, page.Through);
        Assert.False(page.SnapshotRequired);
        var quiet = reader.Since("prs", null, 0, default);
        Assert.Empty(quiet.Events);
        Assert.Equal(3, quiet.Through);
        f.Reopen();
        Assert.Empty(new EngineReader(f.Db, f.Machine).Since("usage", null, page.Through, default).Events);
    }

    [Fact]
    public async Task Snapshot_cut_is_consistent_across_concurrent_commit()
    {
        using var f = new EngineFixture();
        for (var i = 0; i < 205; i++) f.SeedSession($"s{i:D3}");
        Event(f, "session", "s000");
        var reader = new EngineReader(f.Db, f.Machine);
        using var snapshot = reader.Snapshot("sessions", null, default);
        var cut = snapshot.Through;
        await Task.Run(() => { f.SeedSession("new"); Event(f, "session", "new"); }).WaitAsync(TimeSpan.FromSeconds(5));
        var pages = new List<SnapshotPage>();
        do { pages.Add(snapshot.ReadNext()); } while (pages[^1].HasMore);
        Assert.All(pages, p => Assert.Equal(cut, p.Through));
        var ids = pages.SelectMany(p => p.Items).Select(i => i.GetProperty("id").GetString()).ToArray();
        Assert.Equal(205, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.DoesNotContain("new", ids);
        Assert.Contains("new", reader.Since("sessions", null, cut, default).Events.Select(e => e.EntityId));
    }

    [Fact]
    public void Only_real_gap_resnapshots()
    {
        using var f = new EngineFixture();
        for (var i = 0; i < 205; i++) Event(f, "session", $"s{i}");
        var reader = new EngineReader(f.Db, f.Machine);
        var first = reader.Since("sessions", null, 0, default);
        Assert.Equal(200, first.Through);
        Assert.True(first.HasMore);
        var last = reader.Since("sessions", null, first.Through, default);
        Assert.Equal(5, last.Events.Count);
        Assert.False(last.HasMore);
        Assert.Equal("cursor_ahead", Assert.Throws<ArgumentException>(() => reader.Since("sessions", null, 206, default)).Message);
        f.Db.Writer.Write("hole", s => s.Execute("DELETE FROM engine_event WHERE seq=202"));
        Assert.True(reader.Since("sessions", null, 200, default).SnapshotRequired);
        f.Db.Writer.Write("floor", s => s.Execute("UPDATE engine_meta SET retained_after=10"));
        Assert.True(reader.Since("sessions", null, 0, default).SnapshotRequired);
        using var cancel = new CancellationTokenSource();
        var snapshot = reader.Snapshot("sessions", null, cancel.Token);
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => snapshot.ReadNext());
        snapshot.Dispose();
        Assert.Throws<ObjectDisposedException>(() => snapshot.ReadNext());
    }

    [Fact]
    public void Topic_keys_include_linked_entities_and_share_the_same_sequence()
    {
        using var f = new EngineFixture();
        f.SeedSession(); f.SeedRun(); f.SeedStep();
        Event(f, "step", "step", new("s", "r", "step", null, "pending", null));
        Event(f, "handoff", "h", new("s", "r", null, null, "pending", null));
        var reader = new EngineReader(f.Db, f.Machine);
        Assert.Equal(new long[] { 1, 2 }, reader.Since("runs", "r", 0, default).Events.Select(e => e.Seq));
        Assert.Equal(new long[] { 2 }, reader.Since("sessions", "s", 0, default).Events.Select(e => e.Seq));
        using var snapshot = reader.Snapshot("runs", "r", default);
        Assert.Equal(2, snapshot.ReadNext().Items.Count);
        Assert.Empty(reader.Since("runs", "other", 0, default).Events);
        Assert.Throws<ArgumentException>(() => reader.Since("outbox", null, 0, default));
    }

    [Fact]
    public void Display_reads_exclude_secrets_and_enforce_UTF8_page_budgets()
    {
        using var f = new EngineFixture();
        for (var i = 0; i < 8; i++) f.SeedSession($"s{i}");
        f.Db.Writer.Write("large", s => s.Execute("UPDATE engine_session SET title=$title", new SqlArg("$title", new string('界', 4000))));
        var reader = new EngineReader(f.Db, f.Machine);
        using var snapshot = reader.Snapshot("sessions", null, default);
        var count = 0;
        SnapshotPage page;
        do
        {
            page = snapshot.ReadNext();
            count += page.Items.Count;
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, EngineJson.Default.SnapshotPage).Length <= 65536);
            Assert.All(page.Items, item => Assert.True(System.Text.Encoding.UTF8.GetByteCount(item.GetRawText()) <= 32768));
        } while (page.HasMore);
        Assert.Equal(8, count);
        Assert.Null(reader.Entity("session", "missing", default));
        Assert.Throws<ArgumentException>(() => reader.Entity("outbox", "x", default));
        f.Db.Writer.Write("huge", s => s.Execute("UPDATE engine_session SET title=$title WHERE id='s0'", new SqlArg("$title", new string('x', 33000))));
        Assert.Throws<InvalidOperationException>(() => reader.Entity("session", "s0", default));
    }
    [Fact]
    public void Replay_byte_budget_advances_only_past_returned_or_filtered_events()
    {
        using var f = new EngineFixture();
        for (var i = 0; i < 12; i++)
            Event(f, "session", $"s{i}", new(null, null, null, null, new string('x', 12000), null));
        var reader = new EngineReader(f.Db, f.Machine);
        long after = 0;
        var seqs = new List<long>();
        StreamPage page;
        do
        {
            page = reader.Since("sessions", null, after, default);
            Assert.False(page.SnapshotRequired);
            Assert.True(page.Through > after);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, EngineJson.Default.StreamPage).Length <= 65536);
            seqs.AddRange(page.Events.Select(e => e.Seq));
            after = page.Through;
        } while (page.HasMore);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (long)i), seqs);
    }

    [Fact]
    public void A_missing_tail_is_a_gap_but_the_retention_floor_itself_is_valid()
    {
        using var f = new EngineFixture();
        Event(f, "session", "s"); Event(f, "session", "t");
        var reader = new EngineReader(f.Db, f.Machine);
        f.Db.Writer.Write("retain", s => s.Execute("DELETE FROM engine_event WHERE seq=1; UPDATE engine_meta SET retained_after=1"));
        Assert.False(reader.Since("sessions", null, 1, default).SnapshotRequired);
        f.Db.Writer.Write("hole", s => s.Execute("DELETE FROM engine_event WHERE seq=2"));
        Assert.True(reader.Since("usage", null, 1, default).SnapshotRequired);
    }

    [Fact]
    public void All_display_kinds_and_topics_use_allowlisted_current_state()
    {
        using var f = new EngineFixture();
        f.SeedSession(); f.SeedRun(); f.SeedStep();
        f.Db.Writer.Write("display", s =>
        {
            const string columns = "id,version,machine,created_ms,updated_ms,";
            var values = "'x',0,$machine,0,0,";
            void Insert(string kind, string extraColumns, string extraValues) =>
                s.Execute($"INSERT INTO engine_{kind}({columns}{extraColumns}) VALUES({values}{extraValues})", new SqlArg("$machine", f.Machine));
            Insert("needs_me", "session_id,kind,text,options_json,state,answer_json", """'s','question','Question','[]','answered','{"secret":true}'""");
            Insert("message", "sender_kind,to_session_id,text,mode,state", "'window','s','Message','queue','pending'");
            Insert("handoff", "session_id,old_holder,new_holder,old_epoch,next_epoch,state", "'s','a','b',0,1,'pending'");
            Insert("pr", "run_id,repo,state", "'r','repo','open'");
            Insert("tool_call", "session_id,epoch,boundary,boundary_id,tool,input_preview,input_bytes,started_ms,late",
                "'s',0,'local','boundary','read','Object(1)',50,0,0");
            Insert("usage", "session_id,epoch,provider_turn_id,input_tokens,late", "'s',0,'turn',5,0");
            return 0;
        });
        var reader = new EngineReader(f.Db, f.Machine);
        foreach (var (kind, id) in new[] { ("session", "s"), ("run", "r"), ("step", "step"),
                     ("needs_me", "x"), ("message", "x"), ("handoff", "x"), ("pr", "x"), ("tool_call", "x"), ("usage", "x") })
        {
            var item = reader.Entity(kind, id, default)!.Value;
            Assert.Equal(kind, item.GetProperty("entityKind").GetString());
            Assert.False(item.TryGetProperty("answerJson", out _));
            Assert.False(item.TryGetProperty("payloadJson", out _));
            Assert.DoesNotContain("secret", item.GetRawText());
        }
        foreach (var (topic, key, count) in new[] { ("sessions", "s", 2), ("runs", "r", 3),
                     ("needs_me", "x", 1), ("messages", "x", 1), ("prs", "x", 1), ("usage", "x", 2) })
        {
            using var snapshot = reader.Snapshot(topic, key, default);
            Assert.Equal(count, snapshot.ReadNext().Items.Count);
        }
        Assert.Equal("Object(1)", reader.Entity("tool_call", "x", default)!.Value.GetProperty("inputPreview").GetString());
    }

}
