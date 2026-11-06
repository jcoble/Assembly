using Assembly.Application.Common.Database;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class EngineMirrorTests
{
    [Fact]
    public async Task Mac_catches_up_after_remote_work_while_it_was_off()
    {
        await using var h = new TwoHosts();
        await h.Start();
        await h.StartMirror();
        await h.StopMac();
        for (var i = 0; i < 50; i++) h.Remote.Commit(1, "fifty");
        await h.StartMac();
        Assert.Equal("fifty", h.RowTitle);
        Assert.Equal(h.Remote.Through, h.Mac.Fixture.Number("SELECT through FROM engine_mirror WHERE state='live'"));
    }

    [Fact]
    public async Task Reconnect_resumes_from_cursor_without_repeat()
    {
        await using var h = new TwoHosts();
        await h.Start();
        await h.StartMirror();
        h.Remote.Commit(20);
        await h.Reached(20);
        await h.StopMirror();
        h.Remote.Commit(10, "thirty");
        await h.StartMirror();
        Assert.Equal(Enumerable.Range(1, 30).Select(i => (long)i), h.Mac.Fixture.Db.Query(
            "SELECT seq FROM engine_event WHERE machine=$m ORDER BY seq", r => r.Long(0), default,
            new SqlArg("$m", h.Remote.Fixture.Machine)));
        Assert.Equal("thirty", h.RowTitle);
    }

    [Fact]
    public async Task Compacted_past_cursor_resnapshots()
    {
        await using var h = new TwoHosts();
        await h.Start();
        h.Remote.Commit(1);
        await h.StartMirror();
        await h.StopMirror();
        h.Remote.Commit(30, "compacted");
        h.Remote.Fixture.Db.Writer.Write("test.compact", s => s.Execute(
            "DELETE FROM engine_event WHERE seq<25; UPDATE engine_meta SET retained_after=24"));
        await h.StartMirror();
        Assert.Equal("compacted", h.RowTitle);
        Assert.Equal(31, h.Mac.Fixture.Number("SELECT retained_after FROM engine_mirror WHERE state='live'"));
    }

    [Fact]
    public async Task Restored_remote_that_got_busy_resnapshots_and_keeps_no_old_events()
    {
        await using var h = new TwoHosts();
        await h.Start();
        await h.StartMirror();
        h.Remote.Commit(100, "backup");
        await h.Reached(100);
        using var backup = new TempDatabase();
        h.Remote.Fixture.BackupTo(backup.Path);
        h.Remote.Commit(80, "old history");
        await h.Reached(180);
        await h.StopMirror();
        var machine = h.Remote.Fixture.Machine;
        Assert.Equal(180, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_event WHERE machine=$m", new SqlArg("$m", machine)));
        await h.Remote.Restore(backup.Path);
        Assert.Equal(machine, h.Remote.Fixture.Machine);
        Assert.Equal(100, h.Remote.Through);
        h.Remote.Commit(100, "restored", "session.restored");
        await h.StartMirror();
        Assert.Equal("restored", h.RowTitle);
        Assert.Equal(0, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_event WHERE machine=$m", new SqlArg("$m", machine)));
        Assert.Equal(200, h.Mac.Fixture.Number("SELECT retained_after FROM engine_mirror WHERE state='live'"));
    }

    [Fact]
    public async Task Replaced_remote_starts_new_mirror_and_retires_old_machine()
    {
        await using var h = new TwoHosts();
        await h.Start();
        await h.StartMirror();
        h.Remote.Commit(2);
        await h.Reached(2);
        var old = h.Remote.Fixture.Machine;
        await h.StopMirror();
        await h.ReplaceRemote();
        h.Remote.Commit(1, "new machine");
        await h.StartMirror();
        Assert.Equal("retired", h.Mac.Fixture.Text("SELECT state FROM engine_mirror WHERE machine=$m", new SqlArg("$m", old)));
        Assert.Equal(0, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_mirror_row WHERE machine=$m", new SqlArg("$m", old)));
        Assert.Equal(0, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_event WHERE machine=$m", new SqlArg("$m", old)));
        Assert.Equal("new machine", h.RowTitle);
    }

    [Fact]
    public async Task Thirty_megabyte_snapshot_completes_without_filling_queue()
    {
        await using var h = new TwoHosts();
        await h.Start();
        var body = new string('x', 20000);
        h.Remote.Fixture.Db.Writer.Write("test.large", s =>
        {
            for (var i = 0; i < 1600; i++)
                s.Execute("""
                    INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,sender_kind,to_session_id,text,mode,state,sender_label,framed)
                    SELECT $id,0,machine,1000,1000,'system',id,$body,'prompt','failed','Assembly',1
                    FROM engine_session WHERE id='s'
                    """, new("$id", $"large-{i:D4}"), new("$body", body));
            return 0;
        });
        await h.StartMirror();
        Assert.Equal(1601, h.Mac.Fixture.Count("engine_mirror_row"));
        Assert.True(h.Mac.Fixture.Number("SELECT SUM(length(row_json)) FROM engine_mirror_row") > 30 * 1024 * 1024);
        Assert.Equal(1600, h.Mac.Fixture.Number("""
            SELECT COUNT(*) FROM engine_mirror_row WHERE entity_kind='message'
                AND json_extract(row_json,'$.text')=$body AND json_extract(row_json,'$.textComplete')=1
                AND json_extract(row_json,'$.textUtf8Bytes')=20000 AND json_extract(row_json,'$.framed')=1
                AND json_extract(row_json,'$.senderLabel')='Assembly' AND json_extract(row_json,'$.toSessionId')='s'
                AND json_extract(row_json,'$.state')='failed'
            """, new SqlArg("$body", body)));
    }

    [Fact]
    public async Task Socket_drop_reopens_and_catches_up_without_mirror_timer()
    {
        await using var h = new TwoHosts();
        await h.Start();
        await h.StartMirror();
        h.Remote.Endpoint.DropAll();
        h.Remote.Commit(4, "reconnected");
        await h.Reached(4);
        Assert.Equal("reconnected", h.RowTitle);
    }

    [Fact]
    public async Task Mirror_has_no_timer_of_its_own()
    {
        await using var h = new TwoHosts();
        await h.Start();
        var time = new MirrorClock();
        await h.StartMirror(time);
        var calls = h.Remote.MachineCalls;
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, time.TimerRequests);
        Assert.Equal(calls, h.Remote.MachineCalls);
    }

    private sealed class MirrorClock : TimeProvider
    {
        private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = new();
        public int TimerRequests;
        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref TimerRequests);
            return _time.CreateTimer(callback, state, dueTime, period);
        }
        public void Advance(TimeSpan by) => _time.Advance(by);
    }
}
