using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Execution;
using Assembly.Engine.Persistence;
using Assembly.Engine.Reading;
using Assembly.Engine.Retention;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine;

public sealed class MachineSequenceTests
{
    [Fact]
    public void Two_machines_hold_the_same_seq()
    {
        using var f = new EngineFixture();
        LocalEvent(f);
        InsertRemoteEvents(f);
        Assert.Equal(6, f.Count("engine_event"));
        Assert.Equal(2, f.Number("SELECT COUNT(*) FROM engine_event WHERE seq=1"));
        Assert.Equal(2, f.Number("SELECT next_seq FROM engine_meta"));
        Assert.Throws<SqliteException>(() => f.Db.Writer.Write("duplicate", s => s.Execute("""
            INSERT INTO engine_event(id,version,machine,created_ms,updated_ms,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json)
            VALUES('duplicate',0,'remote-w',0,0,1,'changed','run','r',0,0,0,'{}')
            """)));
        var columns = f.Db.Query("PRAGMA index_info(engine_event_machine_seq)", r => r.Text(2), default);
        Assert.Equal(new[] { "machine", "seq" }, columns);
    }

    [Fact]
    public void Local_replay_publication_and_retention_ignore_other_machines()
    {
        using var f = new EngineFixture();
        for (var i = 0; i < 3; i++) LocalEvent(f);
        InsertRemoteEvents(f);
        var page = new EngineReader(f.Db, f.Machine).Since("runs", null, 0, default);
        Assert.Equal(new long[] { 1, 2, 3 }, page.Events.Select(e => e.Seq));
        Assert.All(page.Events, e => Assert.Equal(f.Machine, e.Machine));
        Assert.Equal(3, page.Through);
        Assert.False(page.SnapshotRequired);
        Assert.False(page.HasMore);

        var publisher = new EnginePublisher(f.Db);
        var published = new List<EngineEvent>();
        using var subscription = publisher.Register(published.Add, out _);
        // Drain is synchronous: returning completes the actual committed-log publication.
        publisher.Drain(default);
        Assert.Equal(new long[] { 1, 2, 3 }, published.Select(e => e.Seq));
        Assert.All(published, e => Assert.Equal(f.Machine, e.Machine));
        Assert.Equal(3, publisher.PublishedThrough);
        publisher.Drain(default);
        Assert.Equal(3, published.Count);

        // Cross the real retention threshold; a no-op retention pass would miss a foreign-row delete.
        f.Db.Writer.Write("test.backlog", s => s.Execute("""
            WITH RECURSIVE numbers(n) AS (SELECT 4 UNION ALL SELECT n+1 FROM numbers WHERE n<11000)
            INSERT INTO engine_event(id,version,machine,created_ms,updated_ms,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json)
            SELECT $machine||':'||n,0,$machine,0,0,n,'changed','run','r',0,0,0,'{}' FROM numbers;
            UPDATE engine_meta SET next_seq=11001;
            """, new SqlArg("$machine", f.Machine)));
        var result = f.Db.Writer.Write("retain", s => EngineRetention.Apply(s, 1000, 11000, 11000));
        Assert.Equal(1000, result.RetainedAfter);
        Assert.Equal(1000, result.RemovedEvents);
        Assert.Equal(10000, f.Number("SELECT COUNT(*) FROM engine_event WHERE machine=$machine", new SqlArg("$machine", f.Machine)));
        Assert.Equal(5, f.Count("engine_event WHERE machine='remote-w'"));
        Assert.Equal(11001, f.Number("SELECT next_seq FROM engine_meta"));
    }

    [Fact]
    public void A_remote_event_does_not_fill_a_local_replay_gap()
    {
        using var f = new EngineFixture();
        for (var i = 0; i < 3; i++) LocalEvent(f);
        InsertRemoteEvents(f);
        f.Db.Writer.Write("test.gap", s => s.Execute("DELETE FROM engine_event WHERE machine=$machine AND seq=2", new SqlArg("$machine", f.Machine)));
        var page = new EngineReader(f.Db, f.Machine).Since("runs", null, 0, default);
        Assert.True(page.SnapshotRequired);
        Assert.Equal(1, page.Through);
    }

    private static void LocalEvent(EngineFixture f) => f.Db.Writer.Write("test.local-event", s =>
        EngineJournal.Commit(s, new(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "window"), "r", 0, 0, 1000),
            0, 0, [new("changed", "run", "r", 0, false, new(null, "r", null, null, null, null))], []));

    private static void InsertRemoteEvents(EngineFixture f) => f.Db.Writer.Write("test.remote-events", s => s.Execute("""
        WITH numbers(n) AS (VALUES(1),(2),(3),(4),(5))
        INSERT INTO engine_event(id,version,machine,created_ms,updated_ms,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json)
        SELECT 'remote-w:'||n,0,'remote-w',0,0,n,'changed','run','remote-run',0,0,0,'{}' FROM numbers
        """));
}
