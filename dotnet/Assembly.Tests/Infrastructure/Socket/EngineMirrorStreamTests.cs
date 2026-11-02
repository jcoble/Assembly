using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Retention;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class EngineSocketTests
{
    private async Task MirrorSubscribe(WebSocket socket, int id = 3, long? after = null) =>
        await Send(socket, $$"""{"t":"sub","id":{{id}},"stream":"engine","topic":"mirror"{{(after is null ? "" : $",\"after\":{after}")}}}""");

    [Fact]
    public async Task Mirror_snapshot_then_events_cover_every_mirror_kind()
    {
        SeedMirror();
        Event("session", "s");
        await Published();
        using var socket = await Open();
        await MirrorSubscribe(socket);
        var snapshot = await Read(socket);
        Assert.Equal("engine_snapshot", snapshot.GetProperty("t").GetString());
        var items = snapshot.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "handoff", "message", "needs_me", "pr", "run", "session", "step" },
            items.Select(i => i.GetProperty("entityKind").GetString()).Distinct().Order());
        Assert.DoesNotContain("answerJson", snapshot.GetRawText());
        Assert.DoesNotContain("private-secret", snapshot.GetRawText());
        Assert.Equal("r", items.Single(i => i.GetProperty("entityKind").GetString() == "handoff").GetProperty("runId").GetString());
        Assert.All(items, i => Assert.Equal(_f.Machine, i.GetProperty("machine").GetString()));
        Event("usage", "u");
        var events = await Read(socket);
        Assert.Equal(snapshot.GetProperty("through").GetInt64(), events.GetProperty("after").GetInt64());
        Assert.Equal("usage", Assert.Single(events.GetProperty("events").EnumerateArray()).GetProperty("entityKind").GetString());
        Assert.Empty(events.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task Mirror_events_include_every_kind_and_current_display_rows_only()
    {
        SeedMirror();
        foreach (var kind in new[] { "session", "session", "run", "step", "needs_me", "message", "handoff", "pr", "usage", "tool_call", "effect" })
            Event(kind, kind == "session" ? "s" : kind == "run" ? "r" : kind);
        _f.Db.Writer.Write("current", s => s.Execute("UPDATE engine_session SET version=9 WHERE id='s'"));
        await Published();
        using var socket = await Open();
        await MirrorSubscribe(socket, after: 0);
        var page = await Read(socket);
        Assert.Equal("engine_events", page.GetProperty("t").GetString());
        Assert.True(page.GetProperty("events").GetArrayLength() == 11, page.GetRawText());
        var rows = page.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(7, rows.Length);
        Assert.DoesNotContain(rows, r => r.GetProperty("entityKind").GetString() is "usage" or "tool_call" or "effect");
        Assert.Equal(9, rows.Single(r => r.GetProperty("id").GetString() == "s").GetProperty("version").GetInt64());
        Assert.DoesNotContain("private-secret", page.GetRawText());
    }

    [Fact]
    public async Task Mirror_events_and_rows_share_one_read_snapshot()
    {
        _f.SeedSession();
        Event("session", "s");
        await Published();
        _database.EventPageArmed = true;
        using var socket = await Open();
        await MirrorSubscribe(socket, after: 0);
        await _database.Entered.Task.WaitAsync(Wait);
        _f.Db.Writer.Write("change", s => s.Execute("UPDATE engine_session SET title='new-title',version=9 WHERE id='s'"));
        _database.Release.TrySetResult();
        var page = await Read(socket);
        var row = Assert.Single(page.GetProperty("rows").EnumerateArray());
        Assert.Equal(0, row.GetProperty("version").GetInt64());
        Assert.Equal("Test", row.GetProperty("title").GetString());
        Assert.Equal(9, _f.Number("SELECT version FROM engine_session WHERE id='s'"));
    }

    [Fact]
    public async Task Mirror_never_serves_foreign_source_rows_events_or_watermark()
    {
        _f.SeedSession(); _f.SeedSession("foreign");
        _f.Db.Writer.Write("foreign", s => s.Execute("UPDATE engine_session SET machine='other-machine' WHERE id='foreign'"));
        Event("session", "s");
        await Published();
        _f.Db.Writer.Write("foreign-event", s => s.Execute("""
            INSERT INTO engine_event(id,version,machine,created_ms,updated_ms,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json)
            SELECT 'foreign-event',version,'other-machine',created_ms,updated_ms,1000,kind,entity_kind,'foreign',7,at_ms,late,data_json
            FROM engine_event WHERE machine=$machine AND seq=1
            """, new SqlArg("$machine", _f.Machine)));
        using var socket = await Open();
        await MirrorSubscribe(socket, after: 0);
        var events = await Read(socket);
        Assert.Equal(_f.Machine, events.GetProperty("machine").GetString());
        Assert.Equal(_f.Machine, Assert.Single(events.GetProperty("events").EnumerateArray()).GetProperty("machine").GetString());
        Assert.Equal("s", Assert.Single(events.GetProperty("rows").EnumerateArray()).GetProperty("id").GetString());
        await Send(socket, """{"t":"unsub","id":3}""");
        await MirrorSubscribe(socket);
        Assert.Equal("s", Assert.Single((await Read(socket)).GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        await Send(socket, """{"t":"call","id":5,"feature":"engine","method":"machine","args":{}}""");
        Assert.Equal(1, (await Read(socket)).GetProperty("result").GetProperty("through").GetInt64());
    }

    [Fact]
    public async Task Empty_pages_omit_fingerprint_and_next_is_refused_on_another_topic()
    {
        using var socket = await Open();
        await Send(socket, """{"t":"sub","id":1,"stream":"engine","topic":"sessions","after":0}""");
        var replay = await Read(socket);
        Assert.False(replay.TryGetProperty("at", out _));
        Assert.False(replay.TryGetProperty("rows", out _));
        await Send(socket, """{"t":"sub","id":2,"stream":"engine","topic":"sessions"}""");
        Assert.False((await Read(socket)).TryGetProperty("at", out _));
        await Send(socket, """{"t":"next","id":2}""");
        Assert.Equal("bad_args", (await Read(socket)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mirror_snapshot_waits_for_next_and_holds_one_snapshot()
    {
        LargeSessions(230, 1000);
        Event("session", "s0000");
        await Published();
        using var socket = await Open();
        await MirrorSubscribe(socket);
        var first = await Read(socket);
        Assert.True(first.GetProperty("more").GetBoolean());
        _f.Db.Writer.Write("change", s => s.Execute("UPDATE engine_session SET title='new-title',version=1"));
        Event("session", "s0229");
        await Published();
        // Invalid ack is a receive-loop barrier: no second snapshot or buffered event may precede it.
        await Send(socket, """{"t":"ack","id":999,"stream":"engine","after":0}""");
        Assert.Equal("error", (await Read(socket)).GetProperty("t").GetString());
        await Send(socket, """{"t":"next","id":3}""");
        var second = await Read(socket);
        Assert.Equal("engine_snapshot", second.GetProperty("t").GetString());
        Assert.Equal(first.GetProperty("through").GetInt64(), second.GetProperty("through").GetInt64());
        Assert.Equal(first.GetProperty("at").GetRawText(), second.GetProperty("at").GetRawText());
        Assert.DoesNotContain("new-title", second.GetRawText());
        while (second.GetProperty("more").GetBoolean())
        {
            await Send(socket, """{"t":"next","id":3}""");
            second = await Read(socket);
        }
        var live = await Read(socket);
        Assert.Equal("engine_events", live.GetProperty("t").GetString());
        Assert.Equal("new-title", Assert.Single(live.GetProperty("rows").EnumerateArray()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Mirror_snapshot_and_event_pages_stay_under_one_mebibyte_utf8()
    {
        // Non-ASCII titles exercise encoded JSON bytes rather than string length.
        LargeSessions(120, 4500);
        _f.Db.Writer.Write("events", s =>
        {
            for (var i = 0; i < 120; i++) Journal(s, "session", $"s{i:D4}");
            return 0;
        });
        await Published();
        using var socket = await Open();
        await MirrorSubscribe(socket);
        var total = 0;
        while (true)
        {
            var page = await Read(socket);
            Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText()) <= 1024 * 1024 + 512);
            total += page.GetProperty("items").GetArrayLength();
            if (!page.GetProperty("more").GetBoolean()) break;
            await Send(socket, """{"t":"next","id":3}""");
        }
        Assert.Equal(120, total);
        await Send(socket, """{"t":"unsub","id":3}""");
        await MirrorSubscribe(socket, id: 4, after: 0);
        total = 0;
        long through = 0;
        while (true)
        {
            var page = await Read(socket);
            Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText()) <= 1024 * 1024 + 512);
            Assert.Equal(through, page.GetProperty("after").GetInt64());
            through = page.GetProperty("through").GetInt64();
            total += page.GetProperty("events").GetArrayLength();
            Assert.NotEmpty(page.GetProperty("events").EnumerateArray());
            if (!page.GetProperty("more").GetBoolean()) break;
        }
        Assert.Equal(120, total);
    }

    [Fact]
    public async Task Mirror_key_stray_next_future_cursor_and_wrong_history_are_refused_without_subscription()
    {
        Event("session", "s"); Event("effect", "later");
        await Published();
        using var socket = await Open();
        foreach (var frame in new[] {
            """{"t":"sub","id":3,"stream":"engine","topic":"mirror","key":"s"}""",
            """{"t":"next","id":3}""",
            """{"t":"sub","id":3,"stream":"engine","topic":"mirror","after":3}""",
            """{"t":"sub","id":3,"stream":"engine","topic":"mirror","after":1,"at":{"kind":"other","entityId":"s","version":0}}""",
            """{"t":"sub","id":3,"stream":"engine","topic":"mirror","after":1,"at":{"kind":"changed","entityId":"old-history","version":0}}""",
            """{"t":"sub","id":3,"stream":"engine","topic":"mirror","after":1,"at":{"kind":"changed","entityId":"s","version":7}}""" })
        {
            await Send(socket, frame);
            var error = await Read(socket);
            Assert.Equal("bad_args", error.GetProperty("code").GetString());
        }
        await Send(socket, """{"t":"sub","id":3,"stream":"engine","topic":"mirror","after":1,"at":{"kind":"changed","entityId":"s","version":0}}""");
        Assert.Equal("engine_events", (await Read(socket)).GetProperty("t").GetString());
        await Send(socket, """{"t":"next","id":3}""");
        Assert.Equal("bad_args", (await Read(socket)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("runs")]
    [InlineData("needs_me")]
    [InlineData("messages")]
    [InlineData("prs")]
    [InlineData("usage")]
    [InlineData("mirror")]
    public async Task Every_engine_topic_page_has_fingerprint_and_rejects_another_history(string topic)
    {
        Event("session", "s");
        await Published();
        using var socket = await Open();
        await Send(socket, $$"""{"t":"sub","id":1,"stream":"engine","topic":"{{topic}}","after":0}""");
        var replay = await Read(socket);
        Assert.Equal("changed", replay.GetProperty("at").GetProperty("kind").GetString());
        Assert.Equal("s", replay.GetProperty("at").GetProperty("entityId").GetString());
        if (topic != "mirror") Assert.False(replay.TryGetProperty("rows", out _));
        await Send(socket, $$"""{"t":"sub","id":2,"stream":"engine","topic":"{{topic}}"}""");
        Assert.Equal(replay.GetProperty("at").GetRawText(), (await Read(socket)).GetProperty("at").GetRawText());
        await Send(socket, $$$"""{"t":"sub","id":4,"stream":"engine","topic":"{{{topic}}}","after":1,"at":{"kind":"wrong","entityId":"s","version":0}}""");
        Assert.Equal("Invalid engine cursor", (await Read(socket)).GetProperty("message").GetString());
        Event("effect", "effect");
        for (var i = 0; i < 2; i++)
        {
            var live = await Read(socket);
            Assert.Equal("effect", live.GetProperty("at").GetProperty("entityId").GetString());
        }
    }

    [Fact]
    public async Task Compacted_fingerprint_snapshots_and_machine_call_reports_local_watermark()
    {
        Event("session", "s"); Event("effect", "e");
        await Published();
        _f.Db.Writer.Write("compact", s => s.Execute("DELETE FROM engine_event WHERE seq=1; UPDATE engine_meta SET retained_after=1"));
        using var socket = await Open();
        await Send(socket, """{"t":"sub","id":3,"stream":"engine","topic":"mirror","after":1,"at":{"kind":"old","entityId":"s","version":99}}""");
        Assert.Equal("engine_snapshot", (await Read(socket)).GetProperty("t").GetString());
        await Send(socket, """{"t":"call","id":5,"feature":"engine","method":"machine","args":{}}""");
        var info = (await Read(socket)).GetProperty("result");
        Assert.Equal(_f.Machine, info.GetProperty("machine").GetString());
        Assert.Equal(2, info.GetProperty("through").GetInt64());
    }

    [Fact]
    public async Task Mirror_ack_holds_retention_at_700_after_11000_events()
    {
        using var pin = _runtime.Publisher.TrackReader("fixture-pin", 0, () => { });
        _f.Db.Writer.Write("events", s => { for (var i = 0; i < 11000; i++) Journal(s, "effect", "e"); return 0; });
        await Published();
        using var socket = await Open();
        await MirrorSubscribe(socket, after: 700);
        while ((await Read(socket)).GetProperty("more").GetBoolean()) { }
        await Send(socket, """{"t":"ack","id":3,"stream":"engine","after":700}""");
        await Barrier(socket);
        pin.Dispose();
        var result = _f.Db.Writer.Write("retention", s => EngineRetention.Apply(s, 1000,
            _runtime.Publisher.PublishedThrough, _runtime.Publisher.MinimumReaderAck));
        Assert.Equal(700, result.RetainedAfter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mature_mirror_snapshot_survives_retention_and_releases_its_cut(bool compactedCursor)
    {
        LargeSessions(230, 1000);
        var compacted = _database.Worker.Pass(30000, 20000);
        _f.Db.Writer.Write("history", scope =>
        {
            for (var i = 0; i < 30000; i++) Journal(scope, "effect", "history");
            return 0;
        });
        await Published();
        await compacted.WaitAsync(Wait);
        using var socket = await Open();
        await MirrorSubscribe(socket, after: compactedCursor ? 1 : null);
        var page = await Read(socket);
        Assert.Equal("engine_snapshot", page.GetProperty("t").GetString());
        Assert.True(page.GetProperty("more").GetBoolean());
        var cut = page.GetProperty("through").GetInt64();
        Assert.Equal(30000, cut);
        var fingerprint = page.GetProperty("at").GetRawText();
        var items = page.GetProperty("items").EnumerateArray().ToList();
        // Retention can protect the cut without treating a partial page as a completed wire cursor.
        await Send(socket, $$"""{"t":"ack","id":3,"stream":"engine","after":{{cut}}}""");
        Assert.Equal("bad_args", (await Read(socket)).GetProperty("code").GetString());
        var retained = _database.Worker.Pass(cut + 1);
        _f.Db.Writer.Write("concurrent", scope =>
        {
            scope.Execute("UPDATE engine_session SET title='new-title',version=1 WHERE id='s0229'");
            Journal(scope, "session", "s0229");
            return 0;
        });
        await Published();
        // This observes a committed default-retention worker pass while next is still withheld.
        await retained.WaitAsync(Wait);
        do
        {
            await Send(socket, """{"t":"next","id":3}""");
            page = await Read(socket);
            Assert.Equal("engine_snapshot", page.GetProperty("t").GetString());
            Assert.Equal(cut, page.GetProperty("through").GetInt64());
            Assert.Equal(fingerprint, page.GetProperty("at").GetRawText());
            items.AddRange(page.GetProperty("items").EnumerateArray());
        } while (page.GetProperty("more").GetBoolean());
        Assert.Equal(230, items.Count);
        Assert.All(items, item => Assert.NotEqual("new-title", item.GetProperty("title").GetString()));
        var live = await Read(socket);
        Assert.Equal(cut, live.GetProperty("after").GetInt64());
        Assert.Equal(cut + 1, Assert.Single(live.GetProperty("events").EnumerateArray()).GetProperty("seq").GetInt64());
        Assert.Equal("new-title", Assert.Single(live.GetProperty("rows").EnumerateArray()).GetProperty("title").GetString());
        await Send(socket, $$"""{"t":"ack","id":3,"stream":"engine","after":{{cut}}}""");
        await Barrier(socket); // A repeated buffered event would arrive before this result and fail the barrier.
        Assert.Equal(cut, _runtime.Publisher.MinimumReaderAck);
        using var closed = new CancellationTokenSource(Wait);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closed.Token);
        Assert.Equal(cut + 1, _runtime.Publisher.MinimumReaderAck);
    }

    private sealed class WorkerPassObserver(IAtomicWriter writer) : IAtomicWriter
    {
        private readonly Lock _gate = new();
        private (long Through, long Floor, TaskCompletionSource Done)? _waiting;

        public Task Pass(long through, long floor = 0)
        {
            lock (_gate)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting = (through, floor, done);
                return done.Task;
            }
        }

        public T Write<T>(string what, Func<IWriteScope, T> write)
        {
            if (what != "engine.worker") return writer.Write(what, write);
            var pass = writer.Write(what, scope =>
            {
                var result = write(scope);
                var state = scope.QueryFirst("SELECT next_seq-1,retained_after FROM engine_meta WHERE singleton=1",
                    r => (Through: r.Long(0), Floor: r.Long(1)));
                return (Result: result, state.Through, state.Floor);
            });
            lock (_gate)
                if (_waiting is { } waiting && pass.Through >= waiting.Through && pass.Floor >= waiting.Floor)
                {
                    _waiting = null;
                    waiting.Done.TrySetResult();
                }
            return pass.Result;
        }
    }

    private void Journal(IWriteScope scope, string kind, string id) => EngineJournal.Commit(scope,
        new(Guid.NewGuid(), _f.Machine, new(ActorKind.System, "test"), id, 0, 0, 1000),
        0, 0, [new("changed", kind, id, 0, false, new(null, null, null, null, null, null))], []);

    private void LargeSessions(int count, int characters) => _f.Db.Writer.Write("sessions", s =>
    {
        for (var i = 0; i < count; i++)
            s.Execute("""
                INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,state,activity_state,holder,epoch,cancel_requested)
                VALUES($id,0,$machine,1000,1000,'codex','m','/test',$title,'idle','idle','local',0,0)
                """, new("$id", $"s{i:D4}"), new("$machine", _f.Machine), new("$title", new string('界', characters)));
        return 0;
    });

    private void SeedMirror()
    {
        _f.SeedSession(); _f.SeedRun(); _f.SeedStep();
        _f.Db.Writer.Write("mirror", s => s.Execute("""
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state,answer_json)
            VALUES('needs_me',0,$m,1000,1000,'s','question','Question','[]','answered','{"secret":"private-secret"}');
            INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,sender_kind,to_session_id,text,mode,state)
            VALUES('message',0,$m,1000,1000,'owner','s','Message','normal','failed');
            INSERT INTO engine_handoff(id,version,machine,created_ms,updated_ms,session_id,old_holder,new_holder,old_epoch,next_epoch,state)
            VALUES('handoff',0,$m,1000,1000,'s','local','other',0,1,'requested');
            INSERT INTO engine_pr(id,version,machine,created_ms,updated_ms,run_id,repo,state)
            VALUES('pr',0,$m,1000,1000,'r','repo','open');
            INSERT INTO engine_tool_call(id,version,machine,created_ms,updated_ms,session_id,epoch,boundary,boundary_id,tool,input_preview,input_bytes,started_ms,late)
            VALUES('tool_call',0,$m,1000,1000,'s',0,'acp','b','tool','private-secret',14,1000,0);
            INSERT INTO engine_usage(id,version,machine,created_ms,updated_ms,session_id,epoch,provider_turn_id,late)
            VALUES('usage',0,$m,1000,1000,'s',0,'private-turn',0);
            """, new SqlArg("$m", _f.Machine)));
    }
}
