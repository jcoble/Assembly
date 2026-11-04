using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Messages.Reading;

namespace Assembly.Tests.Engine.Messages;

public sealed class MessageQueryTests
{
    private static readonly MessageScope All = new(null, null, null, null, null, null, "", "time");

    [Fact]
    public void Bounded_consumed_candidate_search_reaches_old_bodies_and_full_labels_and_treats_wildcards_literally()
    {
        using var f = new EngineFixture();
        for (var index = 0; index < 1200; index++) MessageReadFixture.Seed(f, $"m{index:D4}", index,
            text: index == 7 ? "original\nbody needle%_\n  " : "unrelated", sender: index == 8 ? new string('L', 300) + "needle%_" : "Sender");
        var queries = new MessageQueries(f.Db, f.Machine); var scope = All with { Search = "needle%_" }; MessageCursor? cursor = null;
        List<MessageKey> found = []; var calls = 0; var emptyBatches = 0;
        do
        {
            var candidates = queries.Candidates(scope, cursor, 100, default); Assert.InRange(candidates.Items.Count, 0, 100);
            var matched = queries.Match(new(candidates.Items.Select(item => item.Key.Id).ToArray(), scope.Search), default);
            var page = queries.SelectMatches(scope, candidates, matched.Ids.Select(id => new MessageKey(f.Machine, id)).ToArray(), default);
            if (page.Items.Count == 0 && page.Next is not null) emptyBatches++;
            found.AddRange(page.Items.Select(item => item.Key)); calls++;
            cursor = page.Next is null ? null : MessageQueries.DecodeCursor(page.Next);
        } while (cursor is not null);
        Assert.Equal(["m0008", "m0007"], found.Select(key => key.Id)); Assert.True(emptyBatches > 0); Assert.Equal(12, calls);
        Assert.Empty(queries.Match(new(["m0001"], "%_"), default).Ids);
        var body = queries.Body("m0007", default); Assert.Equal("original\nbody needle%_\n  ", body.GetProperty("text").GetString());
        Assert.Equal(new string('L', 300) + "needle%_", queries.Body("m0008", default).GetProperty("senderName").GetString());
    }

    [Fact]
    public void Receipt_changes_do_not_create_unread_and_unrelated_usage_does_not_invalidate_metadata_cursor()
    {
        using var f = new EngineFixture(); MessageReadFixture.Seed(f, "one", 1); MessageReadFixture.Seed(f, "two", 2);
        var query = new MessageQueries(f.Db, f.Machine); var first = query.Candidates(All, null, 1, default);
        var count = query.Count(new(f.Machine, "to"), [], default); Assert.Equal(2, count.Count);
        var seen = count.Cut.Select(cut => new SeenCut(cut.Machine, cut.Through, cut.At)).ToArray();
        f.Db.Writer.Write("unrelated usage", scope => Assembly.Engine.Persistence.EngineJournal.Commit(scope,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "fixture"), "usage", 0, 0, 1000), 1, 0,
            [new("usage.changed", "usage", "usage", 1, false, new(null, null, null, null, null, null))], []));
        Assert.Equal("one", Assert.Single(query.Candidates(All, MessageQueries.DecodeCursor(first.Next!), 1, default).Items).Key.Id);
        f.Db.Writer.Write("receipt", scope =>
        {
            scope.Execute("UPDATE engine_message SET version=version+1,state='delivered_to_turn' WHERE id='one'");
            return Assembly.Engine.Persistence.EngineJournal.Commit(scope, new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "fixture"), "one", 1, 0, 1001), 2, 0,
                [new("message.changed", "message", "one", 2, false, new("to", null, null, "from", "delivered_to_turn", 0))], []);
        });
        Assert.Equal(0, query.Count(new(f.Machine, "to"), seen, default).Count);
        Assert.Throws<InvalidOperationException>(() => query.Candidates(All, MessageQueries.DecodeCursor(first.Next!), 1, default));
    }

    [Fact]
    public void Body_and_match_are_owner_local_bounded_and_cancellable()
    {
        using var f = new EngineFixture(); MessageReadFixture.Seed(f, "m", 1, " whitespace\n");
        var query = new MessageQueries(f.Db, f.Machine);
        Assert.Throws<ArgumentException>(() => query.Match(new(Enumerable.Range(0, 201).Select(i => i.ToString()).ToArray(), "x"), default));
        Assert.Throws<ArgumentException>(() => query.Candidates(All with { Search = new string('x', 257) }, null, 100, default));
        Assert.Throws<ArgumentException>(() => query.Candidates(All with { State = "expired" }, null, 100, default));
        Assert.Throws<KeyNotFoundException>(() => query.Body("missing", default));
        f.Db.Writer.Write("large", scope => scope.Execute("UPDATE engine_message SET text=$text WHERE id='m'", new Assembly.Application.Common.Database.SqlArg("$text", new string('x', 1024 * 1024 + 1))));
        Assert.Throws<InvalidOperationException>(() => query.Body("m", default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => query.Match(new(["m"], "x"), cancelled.Token));
    }
}
