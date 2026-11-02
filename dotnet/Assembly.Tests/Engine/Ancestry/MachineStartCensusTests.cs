using Assembly.Application.Common.Database;
using Assembly.Engine.Sessions;
using Assembly.Engine.Mirror;
using Assembly.Engine.Reading;
using System.Text.Json;

namespace Assembly.Tests.Engine.Ancestry;

public sealed class MachineStartCensusTests
{
    [Fact]
    public void Persisted_live_rows_without_captured_owner_are_not_eligible()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        f.Db.Writer.Write("test.mirror", s => s.Execute("""
            INSERT INTO engine_mirror(machine,name,generation,through,retained_after,state,updated_ms)
            VALUES('offline','saved',1,5,0,'live',0);
            INSERT INTO engine_mirror_row(machine,generation,entity_kind,entity_id,version,row_json)
            VALUES('offline',1,'session','child',1,'{"machine":"offline","parentId":"s","state":"idle"}');
            """));
        Assert.Null(f.Db.Writer.Write("test.check", s => MachineStartCheck.Check(s, "s", 3, 1, [], out _)));
    }

    [Fact]
    public void Final_writer_selects_current_cut_and_counts_relevant_children_after_preparation()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        Snapshot(f, "owner", 3, "[]");
        var captured = new[] { new MachineStartMirrorGeneration("owner", 1) };
        // Network preparation retained only stable generation, not this old SQL cut.
        f.Db.Writer.Write("test.page", s =>
        {
            s.Execute("""
                UPDATE engine_mirror SET through=9,at_json='{"kind":"session.changed","entityId":"child","version":7}'
                WHERE machine='owner'
                """);
            s.Execute("""
                INSERT INTO engine_mirror_row(machine,generation,entity_kind,entity_id,version,row_json)
                VALUES('owner',1,'session','child',1,'{"machine":"owner","parentId":"s","state":"idle"}');
                """);
            return 0;
        });
        f.Db.Writer.Write("test.final-census", s =>
        {
            Assert.Equal("child_limit", MachineStartCheck.Check(s, "s", 3, 1, captured, out var cuts));
            Assert.Equal(new MachineStartCensusCut("owner", 1, 9, new CursorAt("session.changed", "child", 7)), Assert.Single(cuts));
            return 0;
        });
        f.Db.Writer.Write("test.end", s => s.Execute("""
            UPDATE engine_mirror_row SET row_json='{"machine":"owner","parentId":"s","state":"ended"}'
            WHERE entity_id='child'; UPDATE engine_mirror SET through=10 WHERE machine='owner';
            """));
        Assert.Null(f.Db.Writer.Write("test.retry", s => MachineStartCheck.Check(s, "s", 3, 1, captured, out _)));
    }

    [Theory]
    [InlineData("snapshotting", 1)]
    [InlineData("retired", 1)]
    [InlineData("live", 2)]
    public void Missing_or_different_captured_generation_refuses_instead_of_dropping(string state, long generation)
    {
        using var f = new EngineFixture(); f.SeedSession();
        Snapshot(f, "owner", 1, "[]");
        f.Db.Writer.Write("test.loss", s => s.Execute("UPDATE engine_mirror SET state=$state,generation=$g", new SqlArg("$state", state), new SqlArg("$g", generation)));
        Assert.Equal("mirror_unavailable", f.Db.Writer.Write("test.check", s =>
            MachineStartCheck.Check(s, "s", 3, 2, [new("owner", 1)], out _)));
    }

    [Fact]
    public void Exact_batch_deduplicates_ids_and_excludes_other_owners_and_spoofed_rows()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedSession("child", parentId: "s");
        Snapshot(f, "one", 2, """
            [{"entityKind":"session","id":"child","version":1,"machine":"one","parentId":"s","state":"idle"},
             {"entityKind":"session","id":"other","version":1,"machine":"one","parentId":"s","state":"idle"}]
            """);
        Snapshot(f, "two", 4, """
            [{"entityKind":"session","id":"third","version":1,"machine":"two","parentId":"s","state":"idle"}]
            """);
        f.Db.Writer.Write("test.spoofed-row", s => s.Execute("""
            INSERT INTO engine_mirror_row(machine,generation,entity_kind,entity_id,version,row_json)
            VALUES('one',1,'session','spoofed',1,'{"machine":"not-one","parentId":"s","state":"idle"}');
            """));
        Assert.Null(f.Db.Writer.Write("test.only-one", s => MachineStartCheck.Check(s, "s", 3, 3, [new("one", 1)], out _)));
        Assert.Equal("child_limit", f.Db.Writer.Write("test.both", s => MachineStartCheck.Check(s, "s", 3, 3, [new("one", 1), new("two", 1)], out _)));
        Assert.Equal("bad_args", f.Db.Writer.Write("test.duplicate", s => MachineStartCheck.Check(s, "s", 3, 3, [new("one", 1), new("one", 1)], out _)));
        Assert.Equal("bad_args", f.Db.Writer.Write("test.local", s => MachineStartCheck.Check(s, "s", 3, 3, [new(f.Machine, 0)], out _)));
    }

    private static void Snapshot(EngineFixture f, string machine, long through, string rows) => f.Db.Writer.Write("test.snapshot", s =>
    {
        MirrorApply.Open(s, machine, machine, 0); MirrorApply.BeginSnapshot(s, machine, 0);
        using var json = JsonDocument.Parse(rows);
        MirrorApply.SnapshotPage(s, machine, json.RootElement.EnumerateArray().ToArray());
        return MirrorApply.EndSnapshot(s, machine, through, null, 0);
    });
}
