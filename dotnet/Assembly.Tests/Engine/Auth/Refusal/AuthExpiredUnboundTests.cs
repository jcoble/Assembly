using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth.Refusal;

// Engine classification only; actual delayed SessionHost/worker/C6 proof remains with its producer owner.
public sealed class AuthExpiredUnboundTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_original_without_binding_keeps_true_receipt_and_only_negative_block_classification(bool failedOpen)
    {
        await using var f = await Completed(failedOpen); var before = Snapshot(f);
        Assert.Equal(Negative(f), f.Apply()); Assert.Equal(before, Snapshot(f));
        AssertNoAuthority(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Genuine_failed_terminal_gets_only_negative_report_and_never_auth_acceptance(bool failedOpen)
    {
        await using var f = await Completed(failedOpen);
        var seq = f.Core.Rows.Commit(f.Core.Session.SessionId, AuthTerminalReportTests.Terminal(f)).Last(r => r.Kind == "turn").Seq;
        Assert.Equal(Negative(f), f.Core.Db.Writer.Write("test.expired-report", s => AuthTerminalReportTests.Report(f, s, seq)));
        Assert.False(f.Core.Db.Writer.Write("test.expired-accepted", s => EngineCommands.IsAcceptedAuthTerminal(s, f.Core.Session.SessionId, seq, f.Proof)));
        Assert.Equal(2, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id IN ($block,$report)", r => r.Long(0), default, new SqlArg("$block", f.Proof.BlockCommandId.ToString("N")), new("$report", f.Proof.ObservationId.ToString("N"))));
        AssertNoAuthority(f);
    }

    [Fact]
    public async Task Negative_results_replay_before_live_checks_and_changed_fingerprint_conflicts()
    {
        await using var f = await Completed(false); var first = f.Apply(); Assert.Equal(Negative(f), first);
        var seq = f.Core.Rows.Commit(f.Core.Session.SessionId, AuthTerminalReportTests.Terminal(f)).Last(r => r.Kind == "turn").Seq;
        var report = f.Core.Db.Writer.Write("test.expired-first-report", s => AuthTerminalReportTests.Report(f, s, seq)); Assert.Equal(Negative(f), report);
        f.Configured = false; f.Core.Db.Writer.Write("test.expired-move", s => s.Execute("UPDATE engine_session SET holder='remote',epoch=epoch+1 WHERE id=$id", new SqlArg("$id", f.Core.Session.SessionId)));
        Assert.Equal(first, f.Apply()); Assert.Equal(report, f.Core.Db.Writer.Write("test.expired-report-replay", s => AuthTerminalReportTests.Report(f, s, seq)));
        Assert.Equal("command_id_conflict", f.Apply(f.Proof with { ChatRunId = "changed-run" }).Error);
    }

    [Fact]
    public async Task Terminal_and_both_negative_receipts_roll_back_and_exact_batch_can_retry()
    {
        await using var f = await Completed(true); var batch = AuthTerminalReportTests.Terminal(f); var receipts = f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt", r => r.Long(0), default);
        f.Core.Db.Writer.Write("test.expired-journal-fault", s => s.Execute("CREATE TRIGGER fail_expired_report BEFORE INSERT ON engine_command_receipt WHEN NEW.command_id='" + f.Proof.ObservationId.ToString("N") + "' BEGIN SELECT RAISE(ABORT,'expired report fault'); END"));
        Assert.Throws<SqliteException>(() => f.Core.Rows.Commit(f.Core.Session.SessionId, batch, s =>
        { var seq = s.QueryFirst("SELECT MAX(seq) FROM rows WHERE session_id=$session AND kind='turn'", r => r.Long(0), new SqlArg("$session", f.Core.Session.SessionId)); Assert.Equal(Negative(f), AuthTerminalReportTests.Report(f, s, seq)); }));
        Assert.Equal(2, f.Core.Rows.LastSeq(f.Core.Session.SessionId)); Assert.Equal(receipts, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt", r => r.Long(0), default));
        f.Core.Db.Writer.Write("test.expired-journal-unfault", s => s.Execute("DROP TRIGGER fail_expired_report"));
        f.Core.Rows.Commit(f.Core.Session.SessionId, batch, s =>
        { var seq = s.QueryFirst("SELECT MAX(seq) FROM rows WHERE session_id=$session AND kind='turn'", r => r.Long(0), new SqlArg("$session", f.Core.Session.SessionId)); Assert.Equal(Negative(f), AuthTerminalReportTests.Report(f, s, seq)); }); AssertNoAuthority(f);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("user")]
    [InlineData("body")]
    [InlineData("refs")]
    [InlineData("holder")]
    [InlineData("config")]
    [InlineData("epoch")]
    [InlineData("acp")]
    [InlineData("attempt")]
    [InlineData("reservation")]
    [InlineData("time")]
    [InlineData("no-expiry")]
    [InlineData("user-body")]
    [InlineData("ack")]
    [InlineData("binding")]
    [InlineData("provider-id")]
    public async Task Corrupt_or_later_state_cannot_borrow_the_expired_unbound_classification(string change)
    {
        await using var f = await Completed(false);
        switch (change)
        {
            case "claim": f.Proof = f.Proof with { Delivery = f.Proof.Delivery! with { ClaimAttempt = 99 } }; break;
            case "user": f.Proof = f.Proof with { Delivery = f.Proof.Delivery! with { UserRowSeq = 99 } }; break;
            case "epoch": f.Proof = f.Proof with { Epoch = f.Proof.Epoch + 1 }; break;
            case "acp": f.Proof = f.Proof with { AcpSessionId = "other" }; break;
            case "provider-id": f.Proof = f.Proof with { ProviderMessageId = "later-provider" }; break;
            case "config": f.Configured = false; break;
            default:
                f.Core.Db.Writer.Write("test.expired-corruption", s => s.Execute(change switch
                {
                    "body" => "UPDATE engine_message SET text='changed' WHERE id=$message",
                    "refs" => "UPDATE engine_message SET attachment_ids_json='[\"a\",\"b\"]' WHERE id=$message",
                    "holder" => "UPDATE engine_session SET holder='remote' WHERE id=$session",
                    "attempt" => "UPDATE engine_message SET attempt=attempt+1 WHERE id=$message",
                    "reservation" => "UPDATE engine_message SET in_flight=1 WHERE id=$message",
                    "time" => "UPDATE engine_outbox SET updated_ms=0 WHERE id=$effect",
                    "no-expiry" => "UPDATE engine_message SET expires_ms=NULL WHERE id=$message",
                    "user-body" => "UPDATE rows SET payload=json_set(payload,'$.text','changed') WHERE session_id=$session AND kind='user'",
                    "ack" => "UPDATE rows SET payload=json_set(payload,'$.acknowledgment',json('{\"kind\":\"prompt_inserted\",\"providerMessageId\":\"later\"}')) WHERE session_id=$session AND kind='user'",
                    _ => "UPDATE rows SET payload=json_set(payload,'$.authBlocker',json('{\"effectId\":\"other\",\"originalClaimAttempt\":1,\"observationId\":\"00000000-0000-0000-0000-000000000001\",\"needId\":\"other\",\"needVersionAtBlock\":1}')) WHERE session_id=$session AND kind='user'"
                }, new SqlArg("$message", f.Proof.Delivery!.MessageId), new("$session", f.Core.Session.SessionId), new("$effect", f.Effect.Id))); break;
        }
        var result = f.Apply(); Assert.False(result.Applied); Assert.NotEqual("expired_unbound_terminal", result.Error);
        Assert.Equal(0, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Already_bound_refusal_retains_capsule_on_expiry_without_new_need_or_retry(bool failedOpen)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(failedOpen); Assert.True(f.Apply().Applied); Expire(f);
        Assert.True(f.Core.Complete(f.Effect, Receipt(f)).Applied); var data = Data(f); Assert.NotNull(data.AuthRefusal); Assert.Equal(1, data.AuthRefusal.MessageAttempt);
        Assert.Equal("failed", f.Core.State("engine_message", f.Proof.Delivery!.MessageId)); Assert.Equal("expired", f.Core.Db.QueryFirst("SELECT reason FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", f.Proof.Delivery.MessageId)));
        Assert.Equal(1, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default)); Assert.Equal(1, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'", r => r.Long(0), default));
    }

    private static async Task<AuthBlockerTests.Fixture> Completed(bool failedOpen)
    {
        var f = await AuthBlockerTests.Fixture.Create(failedOpen);
        try { Expire(f); Assert.True(f.Core.Complete(f.Effect, Receipt(f)).Applied); AssertNoAuthority(f); return f; } catch { await f.DisposeAsync(); throw; }
    }
    private static void Expire(AuthBlockerTests.Fixture f) => f.Core.Db.Writer.Write("test.expiry-at-completion", s => s.Execute("UPDATE engine_message SET expires_ms=$now WHERE id=$id", new SqlArg("$now", f.Core.Time.GetUtcNow().ToUnixTimeMilliseconds()), new("$id", f.Proof.Delivery!.MessageId)));
    private static EffectReceipt Receipt(AuthBlockerTests.Fixture f) => new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new MessageEffectData("queued", null, "auth_required", new UserRowReference(f.Proof.Delivery!.UserRowSeq, f.Proof.Delivery.UserRunId, f.Proof.Epoch)), EngineJson.Default.MessageEffectData), null);
    private static MessageEffectData Data(AuthBlockerTests.Fixture f) => JsonSerializer.Deserialize(f.Core.Db.QueryFirst("SELECT receipt_json FROM engine_outbox WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", f.Effect.Id))!, EngineJson.Default.EffectReceipt)!.Data.Deserialize(EngineJson.Default.MessageEffectData)!;
    private static CommandResult Negative(AuthBlockerTests.Fixture f) => new(false, false, "expired_unbound_terminal", 0, 0, f.Proof.Epoch);
    private static string Snapshot(AuthBlockerTests.Fixture f) => f.Core.Db.QueryFirst("SELECT json_array(m.version,m.state,m.reason,m.attempt,m.in_flight,o.version,o.state,o.receipt_json) FROM engine_message m JOIN engine_outbox o ON o.id=$effect WHERE m.id=$message", r => r.Text(0), default, new SqlArg("$effect", f.Effect.Id), new("$message", f.Proof.Delivery!.MessageId))!;
    private static void AssertNoAuthority(AuthBlockerTests.Fixture f)
    {
        Assert.Equal("failed", f.Core.State("engine_message", f.Proof.Delivery!.MessageId)); Assert.Equal("expired", f.Core.Db.QueryFirst("SELECT reason FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", f.Proof.Delivery.MessageId)));
        var data = Data(f); Assert.Null(data.AuthRefusal); Assert.Equal("queued", data.State); Assert.Equal("auth_required", data.Reason); Assert.Null(data.ProviderTurnId);
        Assert.Null(f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.AuthBlocker); Assert.Null(f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.Acknowledgment);
        Assert.Equal(0, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default)); Assert.Equal(0, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_provider", r => r.Long(0), default)); Assert.Equal(1, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'", r => r.Long(0), default));
    }
}
