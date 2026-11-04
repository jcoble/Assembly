using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages.Reading;
using Assembly.Engine.Persistence;

namespace Assembly.Tests.Engine.Messages;

public sealed class MessageSeenCutTests
{
    [Fact]
    public void A_resnapshot_invalidates_the_seen_generation_without_counting_old_traffic_as_new_unread()
    {
        using var local = new EngineFixture(); using var owner = new EngineFixture(); MessageReadFixture.Seed(owner, "message", 100);
        MessageReadFixture.Mirror(local, owner); var query = new MessageQueries(local.Db, local.Machine); var session = new MessageSessionKey(owner.Machine, "to");
        var baseline = query.Count(session, [], default); Assert.Equal(1, baseline.Count);
        var cut = Assert.Single(baseline.Cut, item => item.Machine == owner.Machine); Assert.True(cut.Generation > 0);
        var seen = new SeenCut(cut.Machine, cut.Through, cut.At, cut.Generation);
        Assert.Equal(0, query.Count(session, [seen], default).Count);
        MessageReadFixture.Mirror(local, owner);
        var replaced = query.Count(session, [seen], default); Assert.Equal(0, replaced.Count); Assert.True(replaced.Partial); Assert.Empty(replaced.MissingMachines);
        var current = query.Count(session, [], default); Assert.Equal(1, current.Count);
        Assert.NotEqual(cut.Generation, Assert.Single(current.Cut, item => item.Machine == owner.Machine).Generation);
    }

    [Fact]
    public void Legacy_unverifiable_remote_watermarks_are_partial_and_excluded_but_never_seen_sources_count_normally()
    {
        using var local = new EngineFixture(); using var owner = new EngineFixture(); MessageReadFixture.Seed(owner, "message", 100);
        MessageReadFixture.Mirror(local, owner); var query = new MessageQueries(local.Db, local.Machine); var session = new MessageSessionKey(owner.Machine, "to");
        var cut = Assert.Single(query.Count(session, [], default).Cut, item => item.Machine == owner.Machine);
        owner.Db.Writer.Write("usage", scope => EngineJournal.Commit(scope,
            new(Guid.NewGuid(), owner.Machine, new(ActorKind.System, "fixture"), "usage", 0, 0, 1001), 1, 0,
            [new("usage.changed", "usage", "usage", 1, false, new(null, null, null, null, null, null))], []));
        var page = new Assembly.Engine.Reading.EngineReader(owner.Db, owner.Machine).Since("mirror", null, cut.Through, default);
        local.Db.Writer.Write("usage mirror", scope => Assembly.Engine.Mirror.MirrorApply.Events(scope, owner.Machine, page.After, page.Through, page.At, page.Events, page.Rows ?? [], 1001));
        var legacy = query.Count(session, [new(cut.Machine, cut.Through, cut.At)], default);
        Assert.Equal(0, legacy.Count); Assert.True(legacy.Partial);
        var certified = query.Count(session, [new(cut.Machine, cut.Through, cut.At, cut.Generation)], default);
        Assert.Equal(0, certified.Count); Assert.False(certified.Partial);
        var neverSeen = query.Count(session, [], default); Assert.Equal(1, neverSeen.Count); Assert.False(neverSeen.Partial);
    }

    [Fact]
    public void A_nonzero_local_watermark_with_no_retained_fingerprint_is_not_proven_by_two_nulls()
    {
        using var local = new EngineFixture(); MessageReadFixture.Seed(local, "message", 100);
        var query = new MessageQueries(local.Db, local.Machine); var session = new MessageSessionKey(local.Machine, "to");
        var cut = Assert.Single(query.Count(session, [], default).Cut); Assert.Equal(0L, cut.Generation);
        local.Db.Writer.Write("pruned legacy fingerprint", scope => scope.Execute("DELETE FROM engine_event"));
        var unknown = query.Count(session, [new(cut.Machine, cut.Through, null, 0)], default);
        Assert.Equal(0, unknown.Count); Assert.True(unknown.Partial);
        Assert.Equal(1, query.Count(session, [], default).Count);
    }
}
