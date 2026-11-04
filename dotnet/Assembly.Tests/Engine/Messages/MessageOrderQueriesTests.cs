using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Messages.Reading;
using Assembly.Engine.Mirror;
using Assembly.Engine.Reading;

namespace Assembly.Tests.Engine.Messages;

public sealed class MessageOrderQueriesTests
{
    [Fact]
    public void Legacy_null_creation_sequence_is_not_dropped_from_the_sender_stream()
    {
        using var f = new EngineFixture(); var prefix = new string('n', 256);
        MessageReadFixture.Seed(f, "legacy", 100, sender: prefix + "a");
        f.Db.Writer.Write("legacy pruned creation", s => s.Execute("UPDATE engine_message SET message_created_seq=NULL WHERE id='legacy'"));
        MessageReadFixture.Seed(f, "new", 200, sender: prefix + "z");
        var through = f.Number("SELECT next_seq-1 FROM engine_meta"); var at = new EngineReader(f.Db, f.Machine).EventAt(through, default);
        var page = new MessageOrderQueries(f.Db, f.Machine).Sender(new(prefix, through, at), default);
        Assert.Equal(new[] { "legacy", "new" }, page.Items.Select(i => i.Key.Id)); Assert.True(page.End);
        Assert.Null(page.Items[0].CreatedSeq); Assert.NotNull(page.Items[1].CreatedSeq);
        Assert.Null(f.Text("SELECT message_created_seq FROM engine_message WHERE id='legacy'"));
    }

    [Fact]
    public void Whole_historical_framed_label_survives_an_equal_version_projection_upgrade()
    {
        using var local = new EngineFixture(); using var owner = new EngineFixture();
        MessageReadFixture.Seed(owner, "older", 200, sender: "a\0z"); MessageReadFixture.Seed(owner, "newer", 100, sender: "a");
        MessageReadFixture.Mirror(local, owner, omitOrderProof: true);
        var before = local.Text("SELECT row_json FROM engine_mirror_row WHERE machine=$machine AND entity_kind='message' AND entity_id='older'", new SqlArg("$machine", owner.Machine));
        using (var json = JsonDocument.Parse(before!)) { Assert.False(json.RootElement.TryGetProperty("senderNameComplete", out _)); Assert.Equal("a\0z", json.RootElement.GetProperty("senderLabel").GetString()); }
        var reader = new EngineReader(owner.Db, owner.Machine); var through = owner.Number("SELECT next_seq-1 FROM engine_meta");
        var row = reader.Entity("message", "older", default)!.Value;
        local.Db.Writer.Write("same-version projected fields", s => MirrorApply.Events(s, owner.Machine, through, through, reader.EventAt(through, default), [], [row], 1001));
        Assert.Equal(before, local.Text("SELECT row_json FROM engine_mirror_row WHERE machine=$machine AND entity_kind='message' AND entity_id='older'", new SqlArg("$machine", owner.Machine)));
        var scope = new MessageScope(owner.Machine, null, null, null, null, null, "", "sender");
        Assert.Equal(new[] { "newer", "older" }, new MessageQueries(local.Db, local.Machine).DirectNamePage(scope, null, 200, default).Page.Items.Select(i => i.Key.Id));
    }

    [Fact]
    public void Actual_binding_full_SQL_order_preserves_NUL_and_nonASCII_whole_keys()
    {
        using var f = new EngineFixture(); string[] values = ["a", "aa", "a\0z", "A\0zz", "É", "é", "e\u0301", new string('n', 256) + "z", new string('n', 256) + "a"];
        for (var i = 0; i < values.Length; i++) MessageReadFixture.Seed(f, $"m{i}", i, sender: values[i]);
        var scope = new MessageScope(null, null, null, null, null, null, "", "sender"); var query = new MessageQueries(f.Db, f.Machine); MessageCursor? cursor = null; List<string> actual = [];
        do
        {
            var page = query.DirectNamePage(scope, cursor, 1, default).Page; actual.AddRange(page.Items.Select(i => i.Key.Id));
            cursor = page.Next is null ? null : MessageQueries.DecodeCursor(page.Next);
        } while (cursor is not null);
        var expected = f.Db.Query("SELECT id FROM engine_message ORDER BY sender_label COLLATE NOCASE,created_ms DESC,origin_machine COLLATE BINARY,id COLLATE BINARY", r => r.Text(0), default);
        Assert.Equal(expected, actual);
        Assert.True(f.Number("SELECT octet_length(title) FROM engine_session WHERE id='to'") >= 0);
        var projection = new EngineReader(f.Db, f.Machine).Entity("message", "m2", default)!.Value;
        Assert.False(projection.GetProperty("senderNameComplete").GetBoolean());
    }

    [Fact]
    public void Owning_receiver_requires_the_exact_version_and_refuses_oversized_comparison_facts()
    {
        using var f = new EngineFixture(); f.SeedSession("to"); var version = f.Number("SELECT version FROM engine_session WHERE id='to'");
        var query = new MessageOrderQueries(f.Db, f.Machine);
        Assert.Equal("query_changed", Assert.Throws<InvalidOperationException>(() => query.Receiver(new([new("to", version + 1)]), default)).Message);
        f.Db.Writer.Write("large original title", s => s.Execute("UPDATE engine_session SET title=$value WHERE id='to'", new SqlArg("$value", new string('x', 40000))));
        Assert.Equal("message_response_too_large", Assert.Throws<InvalidOperationException>(() => query.Receiver(new([new("to", version)]), default)).Message);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => query.Receiver(new([new("to", version)]), cancel.Token));
    }
    [Fact]
    public void First_blocked_range_uses_NOCASE_not_binary_minimum()
    {
        using var local = new EngineFixture(); using var owner = new EngineFixture();
        MessageReadFixture.Seed(local, "first", 1, sender: "0 first"); MessageReadFixture.Seed(local, "middle", 2, sender: "middle");
        MessageReadFixture.Seed(owner, "a1", 3, sender: new string('a', 256) + "a"); MessageReadFixture.Seed(owner, "a2", 4, sender: new string('a', 256) + "b");
        MessageReadFixture.Seed(owner, "z1", 5, sender: new string('Z', 256) + "a"); MessageReadFixture.Seed(owner, "z2", 6, sender: new string('Z', 256) + "b");
        MessageReadFixture.Mirror(local, owner);
        var scope = new MessageScope(null, null, null, null, null, null, "", "sender"); var page = new MessageQueries(local.Db, local.Machine).DirectNamePage(scope, null, 200, default);
        Assert.Equal(new string('a', 256), page.FirstBlockedBucket); Assert.Equal("first", Assert.Single(page.Page.Items).Key.Id); Assert.NotNull(page.Page.Next);
    }

}
