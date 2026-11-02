using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Preferences;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Engine.Expiry;

public sealed class MessageExpiryTests
{
    [Fact]
    public void Fresh_schema_has_nullable_expiry_and_version_six()
    {
        using var f = new EngineFixture();
        Assert.Equal(6, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_message') WHERE name='expires_ms' AND type='INTEGER' AND [notnull]=0"));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(72000)]
    [InlineData(int.MaxValue)]
    public void Fresh_pending_rows_snapshot_supplied_scope_setting_and_receiving_clock(int minutes)
    {
        using var f = new EngineFixture();
        f.SeedSession(state: "running");
        _ = new PreferenceStore(f.Db);
        var policy = new MessageExpiryPolicy(MessageExpiryPreferences.ReadMinutes);
        var c = Context(f, "message", 123456);
        var result = f.Db.Writer.Write("test.same-scope", s =>
        {
            s.Execute("INSERT INTO preferences VALUES('messageExpiryMinutes',$value)", new SqlArg("$value", minutes.ToString()));
            return JournalSend(s, c, new SendMessage(null, "s", "body", "prompt"), policy);
        });
        Assert.True(result.Applied);
        Assert.Equal(123456L + (long)minutes * 60000, f.Number("SELECT expires_ms FROM engine_message"));
        Assert.Equal("integer", f.Text("SELECT typeof(expires_ms) FROM engine_message"));
    }

    [Fact]
    public void Replay_never_reads_changed_or_corrupt_policy_or_recalculates_deadline()
    {
        using var f = new EngineFixture();
        f.SeedSession(state: "running");
        _ = new PreferenceStore(f.Db);
        var c = Context(f, "message", 1000);
        var command = new SendMessage(null, "s", "body", "prompt");
        var policy = new MessageExpiryPolicy(MessageExpiryPreferences.ReadMinutes);
        var first = f.Db.Writer.Write("test.first", s => JournalSend(s, c, command, policy));
        foreach (var saved in new[] { "45", "null", "not-json" })
        {
            f.Db.Writer.Write("test.change", s => s.Execute("INSERT INTO preferences VALUES('messageExpiryMinutes',$value) ON CONFLICT(key) DO UPDATE SET value=$value", new SqlArg("$value", saved)));
            var replay = f.Db.Writer.Write("test.replay", s => JournalSend(s, c with { NowMs = 99999999 }, command,
                new MessageExpiryPolicy(_ => throw new InvalidOperationException("Replay read policy"))));
            Assert.Equal(first, replay);
            Assert.Equal(1801000, f.Number("SELECT expires_ms FROM engine_message"));
        }
        Assert.Single(f.Events());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"45\"")]
    [InlineData("false")]
    [InlineData("1.5")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("not-json")]
    public void Corrupt_saved_policy_fails_visibly_without_reset_or_partial_send(string json)
    {
        using var f = new EngineFixture();
        f.SeedSession(state: "running");
        _ = new PreferenceStore(f.Db);
        f.Db.Writer.Write("test.corrupt", s => s.Execute("INSERT INTO preferences VALUES('messageExpiryMinutes',$value)", new SqlArg("$value", json)));
        var error = Assert.Throws<CallFailed>(() => f.Db.Writer.Write("test.send", s =>
            JournalSend(s, Context(f, "message"), new SendMessage(null, "s", "body", "prompt"), new(MessageExpiryPreferences.ReadMinutes))));
        Assert.Equal(MessageExpiryPreference.Error, error.Message);
        Assert.Equal(json, f.Text("SELECT value FROM preferences"));
        Assert.Equal(0, f.Count("engine_message"));
        Assert.Equal(0, f.Count("engine_event"));
        Assert.Equal(0, f.Count("engine_command_receipt"));
    }

    [Fact]
    public void Overflow_rolls_back_caller_writes_and_message_effect_journal_receipt()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var error = Assert.Throws<CallFailed>(() => f.Db.Writer.Write("test.overflow", s =>
        {
            s.Execute("UPDATE engine_session SET title='partial'");
            return JournalSend(s, Context(f, "message", long.MaxValue), new(null, "s", "body", "prompt"), new());
        }));
        Assert.Equal("message_expiry_overflow", error.Message);
        Assert.Equal("Test", f.Text("SELECT title FROM engine_session"));
        Assert.Equal(0, f.Count("engine_message")); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Equal(0, f.Count("engine_event")); Assert.Equal(0, f.Count("engine_command_receipt"));
    }

    [Fact]
    public void Due_queued_and_held_rows_expire_once_even_behind_busy_lost_or_need_gates()
    {
        foreach (var state in new[] { "running", "lost", "idle" })
        {
            using var f = Pending(state);
            if (state == "idle")
                f.Db.Writer.Write("test.need", s => s.Execute("""
                    INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state)
                    VALUES('n',1,$machine,0,0,'s','question','wait','[]','open')
                    """, new SqlArg("$machine", f.Machine)));
            List<EffectDraft> effects = [];
            var events = f.Db.Writer.Write("test.release", s => MessageRelease.Next(s, Context(f, "expiry", 1801000), "s", effects));
            Assert.Equal(2, events.Count); Assert.Empty(effects);
            Assert.All(events, e => Assert.Equal("failed", e.Data.State));
            Assert.Equal(2, f.Number("SELECT COUNT(*) FROM engine_message WHERE state='failed' AND reason='expired' AND version=2"));
            var again = Expire(f, 1801000);
            Assert.Empty(again.Events); Assert.Empty(again.Recipients); Assert.False(again.More);
            Assert.Null(Deadline(f));
        }
    }

    [Theory]
    [InlineData("pending", null, false)]
    [InlineData("inflight", null, false)]
    [InlineData("uncertain", null, false)]
    [InlineData("unavailable", null, false)]
    [InlineData("unavailable", 4, false)]
    [InlineData("done", 4, false)]
    [InlineData("failed", 4, false)]
    [InlineData("dead", 4, false)]
    [InlineData("future_state", null, false)]
    [InlineData("done", 0, true)]
    [InlineData("failed", 3, true)]
    [InlineData("dead", null, true)]
    public void Every_unresolved_historical_send_and_Unknown_receipt_blocks_expiry(string state, int? disposition, bool safe)
    {
        using var f = Pending("running", held: false);
        // Message send attempt 3, historical send attempt 1, outbox claim attempt 19.
        f.Db.Writer.Write("test.historical-reservation", s =>
        {
            s.Execute("UPDATE engine_message SET attempt=3");
            return s.Execute("""
                INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt,receipt_json)
                VALUES('send/m/1',9,$machine,0,0,0,'s','send','{"messageId":"m"}',$state,19,$receipt)
                """, new("$machine", f.Machine), new("$state", state), new("$receipt", disposition is null ? null : "{\"disposition\":" + disposition + "}"));
        });
        Assert.Equal(safe ? 1801000 : null, Deadline(f));
        var batch = Expire(f, 1801000);
        Assert.Equal(safe ? 1 : 0, batch.Events.Count); Assert.False(batch.More);
        Assert.Equal(safe ? "failed" : "queued", f.State("engine_message", "m"));
        Assert.Equal(19, f.Number("SELECT attempt FROM engine_outbox"));
    }

    [Theory]
    [InlineData("send/m")]
    [InlineData("send/m/3")]
    public void Exact_legacy_and_current_ID_fallback_protect_missing_payload_bindings(string effectId)
    {
        using var f = Pending("running", held: false);
        f.Db.Writer.Write("test.id-reservation", s => s.Execute("""
            UPDATE engine_message SET attempt=3;
            INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt)
            VALUES($id,0,$machine,0,0,0,'s','send','{}','unavailable',19)
            """, new("$id", effectId), new("$machine", f.Machine)));
        Assert.Empty(Expire(f, 1801000).Events); Assert.Null(Deadline(f));
    }

    [Fact]
    public void Inflight_foreign_and_handoff_rows_never_schedule_or_expire()
    {
        foreach (var variant in new[] { "inflight", "foreign", "handing_off", "other_machine", "remote_holder" })
        {
            using var f = Pending("running", held: false);
            f.Db.Writer.Write("test.exclude", s => s.Execute(variant switch
            {
                "inflight" => "UPDATE engine_message SET in_flight=1",
                "foreign" => "UPDATE engine_session SET state='foreign'",
                "handing_off" => "UPDATE engine_session SET state='handing_off'",
                "other_machine" => "UPDATE engine_message SET machine='other'",
                _ => "UPDATE engine_session SET holder='remote'"
            }));
            Assert.Null(Deadline(f)); Assert.Empty(Expire(f, 1801000).Events);
        }
    }

    [Fact]
    public void Finite_batches_continue_and_rollback_restores_every_message_and_publication()
    {
        using var f = Pending("running", held: false);
        f.Db.Writer.Write("test.backlog", s => s.Execute("""
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<204)
            INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,sender_kind,to_session_id,text,mode,state,expires_ms)
            SELECT 'extra/'||x,1,$machine,1000,1000,'window','s','body','prompt','queued',1801000 FROM n
            """, new SqlArg("$machine", f.Machine)));
        Assert.Throws<InvalidOperationException>(() => f.Db.Writer.Write<int>("test.rollback", s =>
        {
            var expired = MessageExpiry.ExpireDue(s, Context(f, "expiry", 1801000));
            EngineJournal.Commit(s, Context(f, "expiry", 1801000), 0, 0, expired.Events, []);
            throw new InvalidOperationException("injected failure");
        }));
        Assert.Equal(205, f.Number("SELECT COUNT(*) FROM engine_message WHERE state='queued' AND version=1"));
        Assert.Empty(f.Events());
        var first = Expire(f, 1801000); Assert.Equal(100, first.Events.Count); Assert.True(first.More); Assert.Single(first.Recipients);
        var second = Expire(f, 1801000); Assert.Equal(100, second.Events.Count); Assert.True(second.More);
        var last = Expire(f, 1801000); Assert.Equal(5, last.Events.Count); Assert.False(last.More);
        Assert.Empty(Expire(f, 1801000).Events);
    }

    [Fact]
    public void Release_reserves_before_deadline_and_expiry_cannot_steal_reservation()
    {
        using var f = Pending("running", held: false);
        f.Db.Writer.Write("test.idle", s => s.Execute("UPDATE engine_session SET state='idle'"));
        List<EffectDraft> effects = [];
        var changes = f.Db.Writer.Write("test.release", s => MessageRelease.Next(s, Context(f, "release", 1800999), "s", effects));
        Assert.Single(changes); var effect = Assert.Single(effects); Assert.Equal("send/m/1", effect.Id);
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message"));
        Assert.Null(Deadline(f)); Assert.Empty(Expire(f, 1801000).Events);
    }

    [Fact]
    public void Expiry_wins_at_exact_boundary_so_release_cannot_create_send()
    {
        using var f = Pending("running", held: false);
        f.Db.Writer.Write("test.idle", s => s.Execute("UPDATE engine_session SET state='idle'"));
        List<EffectDraft> effects = [];
        var changes = f.Db.Writer.Write("test.release", s => MessageRelease.Next(s, Context(f, "release", 1801000), "s", effects));
        Assert.Single(changes); Assert.Empty(effects); Assert.Equal("expired", f.Text("SELECT reason FROM engine_message"));
        Assert.Equal(0, f.Number("SELECT attempt FROM engine_message"));
    }

    [Fact]
    public void Raw_moved_cleanup_and_held_release_cannot_modify_unresolved_historical_reservation()
    {
        using var f = Pending("running");
        f.Db.Writer.Write("test.unsafe", s => s.Execute("""
            UPDATE engine_message SET framed=0,held_epoch=0;
            UPDATE engine_session SET epoch=1;
            INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt)
            VALUES('old/send',0,$machine,0,0,0,'s','send','{"messageId":"m"}','unavailable',5)
            """, new SqlArg("$machine", f.Machine)));
        List<EffectDraft> effects = [];
        var changes = f.Db.Writer.Write("test.release", s => MessageRelease.Next(s, Context(f, "release", 2000000), "s", effects));
        Assert.DoesNotContain(changes, e => e.EntityId == "m");
        f.Db.Writer.Write("test.held", s => MessageTransitions.ReleaseHeld(s, Context(f, "release", 2000000), "s", true, effects));
        Assert.Equal("queued", f.State("engine_message", "m")); Assert.Equal(1, f.Number("SELECT version FROM engine_message WHERE id='m'"));
    }

    [Fact]
    public void Raw_owner_held_and_system_inserts_share_policy_without_reading_it_for_terminal_history()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running"); f.SeedSession("child", parentId: "s");
        var reads = 0; var policy = new MessageExpiryPolicy(_ => { reads++; return 45; });
        var prepared = OwnerPromptFingerprint.Prepare(f.Machine, new SendOwnerPrompt("s", "raw\0full", "client", [], null), default);
        f.Db.Writer.Write("test.owner", s => MessageTransitions.ApplyOwnerPrompt(s, Context(f, "raw", 2000), prepared, policy));
        f.Db.Writer.Write("test.held", s => MessageTransitions.Apply(s, Context(f, "interrupt", 3000), new SendMessage(null, "s", "held", "interrupt"), expiryPolicy: policy));
        f.Db.Writer.Write("test.need", s => MessageTransitions.NeedResolved(s, Context(f, "need", 4000), "n", "s", "dismissed", null, policy));
        f.Db.Writer.Write("test.child", s => MessageTransitions.ChildEnded(s, Context(f, "child-end", 5000), "child", [], policy));
        Assert.Equal(4, reads);
        Assert.Equal(2702000, f.Number("SELECT expires_ms FROM engine_message WHERE id='raw'"));
        Assert.Equal(2703000, f.Number("SELECT expires_ms FROM engine_message WHERE id='interrupt'"));
        Assert.Equal(2704000, f.Number("SELECT expires_ms FROM engine_message WHERE id='need-answer/n'"));
        Assert.Equal(2705000, f.Number("SELECT expires_ms FROM engine_message WHERE id='child-ended/child'"));
        Assert.Equal("raw\0full", f.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id='raw'", r => System.Text.Encoding.UTF8.GetString(r.Blob(0)), default));
        f.Db.Writer.Write("test.ended", s => s.Execute("UPDATE engine_session SET state='ended' WHERE id='s'"));
        f.SeedSession("later-child", parentId: "s");
        f.Db.Writer.Write("test.history", s => MessageTransitions.ChildEnded(s, Context(f, "ended-child", 6000), "later-child", [], policy));
        Assert.Equal(4, reads); Assert.Null(f.Text("SELECT expires_ms FROM engine_message WHERE id='child-ended/later-child'"));
    }

    private static CommandResult JournalSend(IWriteScope scope, CommandContext context, SendMessage command, MessageExpiryPolicy policy) =>
        EngineJournal.Execute(scope, context, JsonSerializer.Serialize(command, EngineJson.Default.SendMessage),
            () => MessageTransitions.Apply(scope, context, command, expiryPolicy: policy));
    private static CommandContext Context(EngineFixture f, string id, long now = 1000) => new(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "window"), id, 0, 0, now);
    private static EngineFixture Pending(string state, bool held = true)
    {
        var f = new EngineFixture(); f.SeedSession(state: state);
        f.Db.Writer.Write("test.pending", s => s.Execute("""
            INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,sender_kind,to_session_id,text,mode,state,expires_ms)
            VALUES('m',1,$machine,1000,1000,'window','s','body','prompt','queued',1801000)
            """, new SqlArg("$machine", f.Machine)));
        if (held) f.Db.Writer.Write("test.held", s => s.Execute("""
            INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,sender_kind,to_session_id,text,mode,state,expires_ms)
            VALUES('held',1,$machine,1000,1000,'window','s','held body','interrupt','held',1801000)
            """, new SqlArg("$machine", f.Machine)));
        return f;
    }
    private static MessageExpiryBatch Expire(EngineFixture f, long now) => f.Db.Writer.Write("test.expire", s => MessageExpiry.ExpireDue(s, Context(f, "expiry", now)));
    private static long? Deadline(EngineFixture f) => f.Db.Writer.Write("test.deadline", s => MessageExpiry.NextDeadline(s, f.Machine));
}
