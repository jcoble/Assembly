using Assembly.Tests.Engine;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Engine.Delivery;

public sealed class DeliveryAcknowledgmentTests
{
    [Fact]
    public void Fresh_row_format_supports_positive_delivery_acknowledgments()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        Assert.Equal(5, RowStore.Open(db).StoredFormatVersion);
    }

    [Fact]
    public void Actual_format4_upgrades_without_changing_old_payload_or_identity()
    {
        using var file = new TempDatabase();
        using (var connection = new SqliteConnection($"Data Source={file.Path}"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", "row-format4.sql"));
            command.ExecuteNonQuery();
        }
        using var db = TestDatabase.Open(file.Path);
        var before = db.Query("SELECT session_id,seq,run_id,item_id,kind,payload,created_at FROM rows ORDER BY seq",
            r => string.Join("|", Enumerable.Range(0, 7).Select(i => r.IsNull(i) ? "null" : i is 1 or 6 ? r.Long(i).ToString() : r.Text(i))), default);
        var rows = RowStore.Open(db);
        Assert.Equal(5, rows.StoredFormatVersion);
        Assert.Equal(before, db.Query("SELECT session_id,seq,run_id,item_id,kind,payload,created_at FROM rows ORDER BY seq",
            r => string.Join("|", Enumerable.Range(0, 7).Select(i => r.IsNull(i) ? "null" : i is 1 or 6 ? r.Long(i).ToString() : r.Text(i))), default));
        using var scope = db.OpenSnapshot(default);
        var row = rows.UserDeliveryOfClient(scope, "fixture-session", "fixture-client")!;
        Assert.Equal("actual format4 prompt", row.Text); Assert.Null(row.Acknowledgment);
        Assert.Equal("fixture-run", row.RunId); Assert.Equal(1, row.Seq);
        Assert.Contains(db.Query("EXPLAIN QUERY PLAN SELECT seq FROM rows INDEXED BY rows_user_client WHERE session_id='fixture-session' AND json_extract(payload,'$.clientId')='fixture-client' AND kind='user' AND item_id IS NULL ORDER BY seq DESC LIMIT 1", r => r.Text(3), default), value => value.Contains("rows_user_client", StringComparison.Ordinal));
    }

    [Fact]
    public void Omitted_ack_keeps_exact_old_generated_payload_bytes()
    {
        Assert.Equal("{\"text\":\"hi\",\"from\":\"peer\",\"clientId\":\"c\"}",
            JsonSerializer.Serialize(new UserPayload("hi", "peer", "c"), RowJsonContext.Rows.UserPayload));
    }

    [Theory]
    [InlineData("prompt_inserted", "real-provider-message")]
    [InlineData("steer_injected", null)]
    public void Positive_ack_preserves_user_identity_survives_reopen_and_more_than_200_rows(string kind, string? providerId)
    {
        using var file = new TempDatabase();
        UserAcknowledgment ack = new(kind, providerId);
        long seq;
        using (var db = TestDatabase.Open(file.Path))
        {
            var rows = RowStore.Open(db);
            rows.Commit("s", new RowConverter().Apply(new TurnStarted(123, "chat-run", "exact\0body", "original-client")));
            var original = rows.RowsAfter("s", 0)[0]; seq = original.Seq;
            db.Writer.Write("test.ack", s => { rows.AcknowledgeUser(s, "s", "original-client", ack); return 0; });
            var after = rows.RowsAfter("s", 0)[0];
            Assert.Equal(original with { Payload = after.Payload }, after);
            Assert.Equal("exact\0body", after.Payload.GetProperty("text").GetString());
            var raw = after.Payload.GetRawText();
            db.Writer.Write("test.block", s => s.Execute("CREATE TRIGGER block_ack BEFORE UPDATE ON rows BEGIN SELECT RAISE(ABORT,'duplicate write'); END"));
            db.Writer.Write("test.same", s => { rows.AcknowledgeUser(s, "s", "original-client", ack); return 0; });
            Assert.Equal(raw, rows.RowsAfter("s", 0)[0].Payload.GetRawText());
            db.Writer.Write("test.unblock", s => s.Execute("DROP TRIGGER block_ack"));
            for (var i = 0; i < 205; i++) rows.Commit("s", new RowConverter().Apply(new TurnStarted(i, "later-" + i, "later", "later-" + i)));
            Assert.Null(rows.UserRowOfClient("s", "original-client")); // Original convenience reader's bound is unchanged.
        }
        using var reopened = TestDatabase.Open(file.Path); var current = RowStore.Open(reopened);
        using var snapshot = reopened.OpenSnapshot(default);
        Assert.Equal(new UserDeliveryRow(seq, "chat-run", "exact\0body", null, "original-client", ack), current.UserDeliveryOfClient(snapshot, "s", "original-client"));
        using var copyFile = new TempDatabase(); using var copyDb = TestDatabase.Open(copyFile.Path); var copy = RowStore.Open(copyDb);
        foreach (var row in current.RowsAfter("s", 0)) Assert.True(copy.Copy(row));
        Assert.Equal(ack, copy.PageBefore("s", long.MaxValue, 500).Rows.First(r => r.Seq == seq).Payload.Deserialize(RowJsonContext.Rows.UserPayload)!.Acknowledgment);
    }

    [Fact]
    public void Conflicting_ambiguous_and_failed_ack_writes_roll_back_related_work()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path); var rows = RowStore.Open(db);
        rows.Commit("s", new RowConverter().Apply(new TurnStarted(123, "run", "body", "c")));
        db.Writer.Write("test.ack", s => { rows.AcknowledgeUser(s, "s", "c", new("prompt_inserted", "provider-one")); return 0; });
        var before = rows.RowsAfter("s", 0)[0].Payload.GetRawText();
        Assert.Equal("delivery_ack_conflict", Assert.Throws<CallFailed>(() => db.Writer.Write("test.conflict", s =>
        {
            s.Execute("UPDATE sessions SET next_seq=99 WHERE session_id='s'");
            rows.AcknowledgeUser(s, "s", "c", new("prompt_inserted", "provider-two")); return 0;
        })).Message);
        Assert.Equal(2, rows.LastSeq("s")); Assert.Equal(before, rows.RowsAfter("s", 0)[0].Payload.GetRawText());
        rows.Commit("s", new RowConverter().Apply(new TurnStarted(124, "other", "body", "c")));
        Assert.Equal("delivery_ack_conflict", Assert.Throws<CallFailed>(() => db.Writer.Write("test.ambiguous", s => { rows.AcknowledgeUser(s, "s", "c", new("prompt_inserted", "provider-one")); return 0; })).Message);
        rows.Commit("other", new RowConverter().Apply(new TurnStarted(125, "run", "body", "c")));
        db.Writer.Write("test.block", s => s.Execute("CREATE TRIGGER block_ack BEFORE UPDATE ON rows BEGIN SELECT RAISE(ABORT,'ack blocked'); END"));
        Assert.Throws<SqliteException>(() => db.Writer.Write("test.rollback", s =>
        {
            s.Execute("UPDATE sessions SET next_seq=99 WHERE session_id='other'");
            rows.AcknowledgeUser(s, "other", "c", new("steer_injected", null)); return 0;
        }));
        Assert.Equal(2, rows.LastSeq("other"));
        using var snapshot = db.OpenSnapshot(default); Assert.Null(rows.UserDeliveryOfClient(snapshot, "other", "c")!.Acknowledgment);
    }
}
