using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.Providers;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth;

// Engine/A30 completion proof only; real host terminal producer ordering is verified separately.
public sealed class AuthCompletionProofTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_first_keeps_one_positive_ack_and_the_original_receipt_while_selecting_the_final_auth_outcome(bool responseKnownAtTerminal)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false);
        f.Proof = f.Proof with { ProviderMessageId = responseKnownAtTerminal ? "actual-provider" : null };
        CommitReport(f);
        var result = f.Core.Complete(f.Effect, Receipt(f)); Assert.True(result.Applied, result.Error);
        AssertInsertedAuthFailure(f);
        var need = f.Core.Db.QueryFirst("SELECT id,version,CAST(text AS BLOB) FROM engine_needs_me WHERE session_id=$id", r =>
            (Id: r.Text(0), Version: r.Long(1), Text: System.Text.Encoding.UTF8.GetString(r.Blob(2))), default, new SqlArg("$id", f.Core.Session.SessionId));
        Assert.Contains("Your last message reached claude before sign-in was needed and was not sent again.", need.Text);
        var started = f.Core.Db.Writer.Write("test.notice-is-still-auth-shape", scope => f.Commands.Apply(scope,
            f.Core.Context("recovery-operation", 0) with { Actor = new(ActorKind.Window, "owner"), Epoch = 0 }, new StartSignIn("claude", "", null, need.Id)));
        Assert.True(started.Applied, started.Error);
    }

    [Fact]
    public async Task Completion_first_requires_its_exact_positive_receipt_and_ack_before_closed_terminal_finalization()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); f.Proof = f.Proof with { ProviderMessageId = "actual-provider" };
        Assert.True(f.Core.Complete(f.Effect, Receipt(f)).Applied); Assert.Equal("next_turn", f.Core.State("engine_message", f.Proof.Delivery!.MessageId));
        CommitReport(f); AssertInsertedAuthFailure(f);
    }

    [Theory]
    [InlineData("ack")]
    [InlineData("message")]
    [InlineData("receipt")]
    public async Task Positive_ack_final_message_receipt_and_notice_remain_one_atomic_writer(string fault)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); f.Proof = f.Proof with { ProviderMessageId = "actual-provider" }; CommitReport(f);
        var needBefore = f.Core.Db.QueryFirst("SELECT text FROM engine_needs_me", r => r.Text(0), default);
        f.Core.Db.Writer.Write("test.auth-positive-fault", scope => scope.Execute(fault switch
        {
            "ack" => "CREATE TRIGGER auth_positive_fault BEFORE UPDATE OF payload ON rows WHEN json_type(NEW.payload,'$.acknowledgment')='object' BEGIN SELECT RAISE(ABORT,'ack fault'); END",
            "message" => "CREATE TRIGGER auth_positive_fault BEFORE UPDATE OF state ON engine_message WHEN NEW.state='failed' AND NEW.reason='inserted_before_sign_in' BEGIN SELECT RAISE(ABORT,'message fault'); END",
            _ => "CREATE TRIGGER auth_positive_fault BEFORE UPDATE OF receipt_json ON engine_outbox WHEN json_extract(NEW.receipt_json,'$.disposition')=0 BEGIN SELECT RAISE(ABORT,'receipt fault'); END"
        }));
        Assert.Throws<SqliteException>(() => f.Core.Complete(f.Effect, Receipt(f)));
        Assert.Equal("queued", f.Core.State("engine_message", f.Proof.Delivery!.MessageId)); Assert.Equal("inflight", f.Core.State("engine_outbox", f.Effect.Id));
        Assert.Null(f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.Acknowledgment);
        Assert.Equal(needBefore, f.Core.Db.QueryFirst("SELECT text FROM engine_needs_me", r => r.Text(0), default));
        f.Core.Db.Writer.Write("test.unfault-auth-positive", scope => scope.Execute("DROP TRIGGER auth_positive_fault"));
        Assert.True(f.Core.Complete(f.Effect, Receipt(f)).Applied); AssertInsertedAuthFailure(f);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("fingerprint")]
    public async Task A_conflicting_retained_proof_or_positive_identity_cannot_be_guessed_as_a_final_outcome(string change)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); f.Proof = f.Proof with { ProviderMessageId = "actual-provider" }; CommitReport(f);
        if (change == "fingerprint") f.Core.Db.Writer.Write("test.corrupt-auth-receipt", scope => scope.Execute("UPDATE engine_command_receipt SET fingerprint='changed' WHERE command_id=$id", new SqlArg("$id", f.Proof.ObservationId.ToString("N"))));
        Assert.Throws<CallFailed>(() => f.Core.Complete(f.Effect, Receipt(f, change == "provider" ? "another-provider" : "actual-provider")));
        Assert.Equal("queued", f.Core.State("engine_message", f.Proof.Delivery!.MessageId)); Assert.Equal("inflight", f.Core.State("engine_outbox", f.Effect.Id));
        Assert.Null(f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.Acknowledgment);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("reason")]
    [InlineData("provider")]
    [InlineData("ack")]
    public async Task Completion_callback_cannot_inject_a_transition_outside_the_exact_accepted_positive_auth_projection(string change)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); f.Proof = f.Proof with { ProviderMessageId = "actual-provider" }; CommitReport(f);
        var commands = new EngineCommands(f.Core.Runtime.Machine, new(() => 5, () => 10), projectCompletion: (scope, _, _, _, _) =>
        {
            if (change != "ack") f.Core.Rows.AcknowledgeUser(scope, f.Core.Session.SessionId, f.Proof.ClientId!, new("prompt_inserted", "actual-provider"));
            return new MessageOutcome(change == "state" ? "queued" : "failed", change == "provider" ? "another-provider" : "actual-provider", change == "reason" ? "arbitrary" : "inserted_before_sign_in");
        });
        Assert.Throws<CallFailed>(() => f.Core.Db.Writer.Write("test.bad-auth-projection", scope => commands.Apply(scope,
            f.Core.Context(f.Effect.Id, f.Core.Number("SELECT version FROM engine_outbox WHERE id=$id", f.Effect.Id)), new CompleteEffect(Receipt(f)))));
        Assert.Equal("queued", f.Core.State("engine_message", f.Proof.Delivery!.MessageId)); Assert.Equal("inflight", f.Core.State("engine_outbox", f.Effect.Id));
        Assert.Null(f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.Acknowledgment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconciliation_uses_only_an_exact_retained_insertion_proof_across_a_new_claim_and_never_sends_again(bool positive)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); f.Proof = f.Proof with { ProviderMessageId = positive ? "actual-provider" : null }; CommitReport(f);
        Assert.True(f.Core.Apply(new RecoverEffect(), f.Effect.Id, f.Core.Number("SELECT version FROM engine_outbox WHERE id=$id", f.Effect.Id)).Applied);
        var reclaimed = f.Core.ClaimEffect(f.Effect.Id); Assert.True(reclaimed.Attempt > f.Proof.Delivery!.ClaimAttempt);
        var reconciled = await f.Core.Effects.ReconcileAsync(reclaimed, default);
        Assert.Equal(positive ? EffectDisposition.Confirmed : EffectDisposition.Unknown, reconciled.Disposition);
        Assert.Equal(0, f.Core.Launcher.Launches);
        if (positive) { Assert.True(f.Core.Complete(reclaimed, reconciled).Applied); AssertInsertedAuthFailure(f); }
        else { Assert.Equal("queued", f.Core.State("engine_message", f.Proof.Delivery.MessageId)); Assert.Null(f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.Acknowledgment); }
    }

    [Fact]
    public async Task Old_null_terminal_proof_keeps_the_ordinary_positive_completion_and_ack_unchanged()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false);
        Assert.True(f.Core.Complete(f.Effect, Receipt(f)).Applied);
        Assert.Equal("next_turn", f.Core.State("engine_message", f.Proof.Delivery!.MessageId));
        Assert.Equal("actual-provider", f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.Acknowledgment?.ProviderMessageId);
        Assert.Equal(0, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
    }

    [Fact]
    public async Task Failed_open_Version2_can_never_supply_positive_insertion_for_its_original_effect()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(true); CommitReport(f);
        Assert.Throws<CallFailed>(() => f.Core.Complete(f.Effect, Receipt(f)));
        Assert.Null(f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.Acknowledgment);
        Assert.Equal("inflight", f.Core.State("engine_outbox", f.Effect.Id));
    }

    [Fact]
    public async Task Identical_original_completion_replays_without_a_second_ack_or_changed_original_receipt()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); f.Proof = f.Proof with { ProviderMessageId = "actual-provider" }; CommitReport(f);
        var context = f.Core.Context(f.Effect.Id, f.Core.Number("SELECT version FROM engine_outbox WHERE id=$id", f.Effect.Id)); var command = new CompleteEffect(Receipt(f));
        var first = f.Core.Db.Writer.Write("test.auth-complete", scope => f.Core.Commands.Apply(scope, context, command)); Assert.True(first.Applied);
        var raw = f.Core.Db.QueryFirst("SELECT receipt_json FROM engine_outbox WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", f.Effect.Id));
        Assert.Equal(first, f.Core.Db.Writer.Write("test.auth-complete-replay", scope => f.Core.Commands.Apply(scope, context, command)));
        Assert.Equal(raw, f.Core.Db.QueryFirst("SELECT receipt_json FROM engine_outbox WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", f.Effect.Id))); AssertInsertedAuthFailure(f);
    }

    [Fact]
    public async Task Pruned_actual_terminal_evidence_stays_unknown_during_reconciliation_without_provider_work()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); f.Proof = f.Proof with { ProviderMessageId = "actual-provider" }; CommitReport(f);
        f.Core.Db.Writer.Write("test.prune-auth-proof", scope => scope.Execute("DELETE FROM rows WHERE session_id=$id AND kind='turn' AND json_type(payload,'$.authFailure')='object'", new SqlArg("$id", f.Core.Session.SessionId)));
        var reconciled = await f.Core.Effects.ReconcileAsync(f.Effect, default); Assert.Equal(EffectDisposition.Unknown, reconciled.Disposition); Assert.Equal(0, f.Core.Launcher.Launches);
    }

    private static void CommitReport(AuthBlockerTests.Fixture f)
    {
        var batch = AuthTerminalReportTests.Terminal(f); var seq = f.Core.Rows.LastSeq(f.Core.Session.SessionId) + batch.Rows.FindIndex(r => r.Kind == "turn") + 1;
        f.Core.Rows.Commit(f.Core.Session.SessionId, batch, scope => { var result = AuthTerminalReportTests.Report(f, scope, seq); Assert.True(result.Applied, result.Error); });
    }
    private static EffectReceipt Receipt(AuthBlockerTests.Fixture f, string provider = "actual-provider") => new(EffectDisposition.Confirmed,
        JsonSerializer.SerializeToElement(new MessageEffectData("next_turn", provider, null, new UserRowReference(f.Proof.Delivery!.UserRowSeq, f.Proof.Delivery.UserRunId, f.Proof.Epoch)), EngineJson.Default.MessageEffectData), null);
    private static void AssertInsertedAuthFailure(AuthBlockerTests.Fixture f)
    {
        Assert.Equal("failed", f.Core.State("engine_message", f.Proof.Delivery!.MessageId)); Assert.Equal("done", f.Core.State("engine_outbox", f.Effect.Id));
        Assert.Equal("inserted_before_sign_in", f.Core.Db.QueryFirst("SELECT reason FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", f.Proof.Delivery.MessageId)));
        var row = f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!;
        Assert.Equal("exact\0body", row.Text); Assert.Equal("R1", row.RunId); Assert.Equal(new UserAcknowledgment("prompt_inserted", "actual-provider"), row.Acknowledgment);
        var receipt = f.Core.Db.QueryFirst("SELECT receipt_json FROM engine_outbox WHERE id=$id", r => JsonSerializer.Deserialize(r.Text(0), EngineJson.Default.EffectReceipt), default, new SqlArg("$id", f.Effect.Id))!;
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition); var data = receipt.Data.Deserialize(EngineJson.Default.MessageEffectData)!;
        Assert.Equal("next_turn", data.State); Assert.Equal("actual-provider", data.ProviderTurnId); Assert.Null(data.Reason);
        Assert.Equal(1, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE session_id=$id", r => r.Long(0), default, new SqlArg("$id", f.Core.Session.SessionId)));
    }
}
