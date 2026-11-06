using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Draft.SessionsList;

/// <summary>The rail's branch is the Engine's, and the hub tells who listens which rows this machine produced.</summary>
public sealed partial class SessionRailTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RailBranchIsTheEnginesNotAnOldTurnsStats(bool remote)
    {
        var session = Parent(machine: remote ? "workbox-test" : null);
        Assert.Null(Entry(Read(), session).Branch);
        var stats = JsonSerializer.SerializeToElement(new { status = "completed", branch = "old-from-stats" });
        if (remote)
            Assert.True(_hub.PublishCopy(new StoredRow(session, 1, "run-1", null, RowKind.Stats, stats, 10, PrevSeq: 0)));
        else
            _hub.Publish(session, Batch((null, RowKind.Stats, new { status = "completed", branch = "old-from-stats" }), 10));
        Assert.Null(Entry(Read(), session).Branch);

        var facade = _sessions.Get(session)!;
        _database.Writer.Write("test.branch", scope => remote
            ? scope.Execute("UPDATE engine_mirror_row SET row_json=json_set(row_json,'$.provider',$provider,'$.folder','/recorded/remote','$.updatedMs',$updated,'$.branch','real') WHERE entity_id=$id",
                new("$provider", facade.Agent), new("$updated", facade.UpdatedAt), new("$id", session))
            : scope.Execute("UPDATE engine_session SET branch='real' WHERE id=$id", new Assembly.Application.Common.Database.SqlArg("$id", session)));
        var known = Assert.Single(Read().Sessions, row => row.SessionId == session && row.Source is not null);
        Assert.Equal("real", known.Branch);
    }

    [Fact]
    public void CommittedHearsRowsThisMachineProducedNotCopies()
    {
        var session = Parent();
        var heard = new List<string>();
        _hub.Committed += (id, rows) => heard.AddRange(rows.Select(row => id + ":" + row.Kind));

        _hub.Publish(session, Batch(("t1", RowKind.Tool, new { state = "completed" }), 10));
        Assert.Equal([session + ":" + RowKind.Tool], heard);

        var copied = Parent(machine: "workbox-test");
        Assert.True(_hub.PublishCopy(new StoredRow(copied, 1, "run-1", "t1", RowKind.Tool, JsonSerializer.SerializeToElement(new { state = "completed" }), 10, PrevSeq: 0)));
        Assert.Single(heard);
    }
}
