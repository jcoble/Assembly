using System.Text;
using Assembly.Engine.Messages.Reading;

namespace Assembly.Tests.Engine.Messages;

public sealed class MessageLocalOrderTests
{
    private static readonly MessageScope All = new(null, null, null, null, null, null, "", "sender");

    [Fact]
    public void Full_local_sender_tails_order_before_time_ties_without_putting_large_labels_in_the_cursor()
    {
        using var f = new EngineFixture(); var prefix = new string('n', 70000);
        MessageReadFixture.Seed(f, "older-a", 100, sender: prefix + "a");
        MessageReadFixture.Seed(f, "newer-z", 200, sender: prefix + "z");
        var query = new MessageQueries(f.Db, f.Machine); var first = query.Candidates(All, null, 1, default);
        Assert.Equal("older-a", Assert.Single(first.Items).Key.Id); Assert.Equal(new string('n', 256), first.Items[0].SenderName);
        Assert.NotNull(first.Next); Assert.DoesNotContain(prefix, Encoding.UTF8.GetString(Convert.FromBase64String(first.Next!)));
        Assert.True(Encoding.UTF8.GetByteCount(first.Next!) < Encoding.UTF8.GetByteCount(prefix));
        var second = query.Candidates(All, MessageQueries.DecodeCursor(first.Next!), 1, default);
        Assert.Equal("newer-z", Assert.Single(second.Items).Key.Id); Assert.Null(second.Next);
        Assert.Empty(query.Candidates(All, second.Captured, 1, default).Items);
    }

    [Fact]
    public void Current_owning_local_recipient_full_titles_order_across_a_shared_preview_prefix()
    {
        using var f = new EngineFixture(); var prefix = new string('r', 70000);
        MessageReadFixture.Seed(f, "older-a", 100); MessageReadFixture.Seed(f, "newer-z", 200);
        f.SeedSession("receiver-a"); f.SeedSession("receiver-z");
        f.Db.Writer.Write("actual receiver titles", scope => scope.Execute("""
            UPDATE engine_session SET title=$a WHERE id='receiver-a';
            UPDATE engine_session SET title=$z WHERE id='receiver-z';
            UPDATE engine_message SET to_session_id='receiver-a' WHERE id='older-a';
            UPDATE engine_message SET to_session_id='receiver-z' WHERE id='newer-z';
            """, new Assembly.Application.Common.Database.SqlArg("$a", prefix + "a"), new("$z", prefix + "z")));
        var scope = All with { Sort = "receiver" }; var query = new MessageQueries(f.Db, f.Machine); var first = query.Candidates(scope, null, 1, default);
        Assert.Equal("older-a", Assert.Single(first.Items).Key.Id); Assert.Equal(new string('r', 256), first.Items[0].ReceiverName);
        Assert.Equal("newer-z", Assert.Single(query.Candidates(scope, MessageQueries.DecodeCursor(first.Next!), 1, default).Items).Key.Id);
        Assert.DoesNotContain(prefix, Encoding.UTF8.GetString(Convert.FromBase64String(first.Next!)));
    }

    [Fact]
    public void A_missing_logical_anchor_refuses_instead_of_silently_restarting_or_exhausting_the_query()
    {
        using var f = new EngineFixture(); MessageReadFixture.Seed(f, "first", 100); MessageReadFixture.Seed(f, "second", 200);
        var query = new MessageQueries(f.Db, f.Machine); var first = query.Candidates(All, null, 1, default);
        var cursor = MessageQueries.DecodeCursor(first.Next!); var missing = cursor with { After = new(cursor.After!.OriginCreatedMs, cursor.After.OriginMachine, "absent") };
        var failure = Assert.Throws<InvalidOperationException>(() => query.Candidates(All, missing, 1, default)); Assert.Equal("query_changed", failure.Message);
    }

    [Fact]
    public void An_old_opaque_name_cursor_is_rejected_without_a_compatibility_restart_path()
    {
        var old = Convert.ToBase64String(Encoding.UTF8.GetBytes("""
            {"queryHash":"old","cuts":[],"after":{"name":"old full label","originCreatedMs":1,"originMachine":"owner","id":"m"}}
            """));
        Assert.Throws<ArgumentException>(() => MessageQueries.DecodeCursor(old));
    }
}
