using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Providers;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth.Refusal;

// Engine/A30 receipt proof; this fixture does not activate real terminal producers or retry provider I/O.
public sealed class AuthRefusalCapsuleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_bound_open_need_is_retained_on_the_true_original_auth_refusal_receipt(bool failedOpen)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(failedOpen); Assert.True(f.Apply().Applied);
        var blocker = f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.AuthBlocker!;
        Assert.True(f.Core.Complete(f.Effect, Receipt(f)).Applied);
        var result = Data(f); Assert.Equal(new AuthRefusalReceipt(1, blocker), result.AuthRefusal); AssertOriginal(f, result);
        Assert.Equal("queued", f.Core.State("engine_message", f.Proof.Delivery!.MessageId)); Assert.Equal("auth_required", f.Core.Db.QueryFirst("SELECT reason FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", f.Proof.Delivery.MessageId)));
        Assert.Equal("open", f.Core.State("engine_needs_me", blocker.NeedId)); Assert.Equal(1, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
        Assert.Equal(1, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'", r => r.Long(0), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpired_completion_without_raw_block_uses_its_actual_command_observation_and_no_fabricated_raw_authority(bool failedOpen)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(failedOpen); var context = f.Core.Context(f.Effect.Id, 1);
        Assert.True(f.Core.Db.Writer.Write("test.completion-only-auth-refusal", s => f.Core.Commands.Apply(s, context, new CompleteEffect(Receipt(f)))).Applied);
        var data = Data(f); AssertOriginal(f, data); Assert.NotNull(data.AuthRefusal); var blocker = Blocker(f);
        Assert.Equal(data.AuthRefusal.Blocker, blocker); Assert.Equal(context.CommandId, blocker.ObservationId); Assert.Null(blocker.BlockCommandId);
        Assert.Equal(f.Effect.Id, blocker.EffectId); Assert.Equal(f.Effect.Attempt, blocker.OriginalClaimAttempt); Assert.Equal(1, data.AuthRefusal.MessageAttempt);
        Assert.Equal("open", f.Core.State("engine_needs_me", blocker.NeedId)); Assert.Equal("needs_sign_in", f.Core.Db.QueryFirst("SELECT auth_state FROM engine_provider WHERE id='claude/'", r => r.Text(0), default));
        Assert.Equal(1, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default)); Assert.Equal(0, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='probe_provider'", r => r.Long(0), default));
    }

    [Fact]
    public async Task Completion_created_need_provider_and_user_binding_roll_back_with_its_capsule()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); var context = f.Core.Context(f.Effect.Id, 1); var receipt = Receipt(f);
        f.Core.Db.Writer.Write("test.completion-capsule-fault", s => s.Execute("CREATE TRIGGER fail_completion_capsule BEFORE UPDATE OF receipt_json ON engine_outbox WHEN json_type(NEW.receipt_json,'$.data.authRefusal')='object' BEGIN SELECT RAISE(ABORT,'capsule fault'); END"));
        Assert.Throws<SqliteException>(() => f.Core.Db.Writer.Write("test.completion-capsule-rollback", s => f.Core.Commands.Apply(s, context, new CompleteEffect(receipt))));
        f.AssertUnblocked(); Assert.Equal("inflight", f.Core.State("engine_outbox", f.Effect.Id));
        f.Core.Db.Writer.Write("test.completion-capsule-unfault", s => s.Execute("DROP TRIGGER fail_completion_capsule"));
        Assert.True(f.Core.Db.Writer.Write("test.completion-capsule-exact-retry", s => f.Core.Commands.Apply(s, context, new CompleteEffect(receipt))).Applied); Assert.NotNull(Data(f).AuthRefusal);
        Assert.Equal(1, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
    }

    [Fact]
    public async Task Prior_login_lineage_releases_once_but_capsule_keeps_original_message_attempt_before_E2_reservation()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); Assert.True(f.Apply().Applied); SignedIn(f);
        Assert.Equal(1, f.Core.Number("SELECT attempt FROM engine_message WHERE id=$id", f.Proof.Delivery!.MessageId));
        Assert.True(f.Core.Complete(f.Effect, Receipt(f)).Applied); var data = Data(f); AssertOriginal(f, data);
        Assert.Equal(1, data.AuthRefusal?.MessageAttempt); Assert.Equal(f.Effect.Id, data.AuthRefusal?.Blocker.EffectId);
        Assert.Equal(2, f.Core.Number("SELECT attempt FROM engine_message WHERE id=$id", f.Proof.Delivery.MessageId));
        Assert.Equal("not_delivered", f.Core.Db.QueryFirst("SELECT reason FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", f.Proof.Delivery.MessageId)));
        Assert.Equal(2, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'", r => r.Long(0), default));
        Assert.Equal("signed_in", f.Core.Db.QueryFirst("SELECT auth_state FROM engine_provider WHERE id='claude/'", r => r.Text(0), default));
        Assert.Equal(0, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'", r => r.Long(0), default));
    }

    [Fact]
    public async Task Dismissed_lineage_fails_only_the_known_original_without_reopening_or_retrying()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); Assert.True(f.Apply().Applied); var need = Blocker(f).NeedId;
        Assert.True(f.Core.Apply(new ResolveNeed("dismissed", null), need, 1, new(ActorKind.Window, "owner")).Applied);
        Assert.True(f.Core.Complete(f.Effect, Receipt(f)).Applied); AssertOriginal(f, Data(f)); Assert.NotNull(Data(f).AuthRefusal);
        Assert.Equal("failed", f.Core.State("engine_message", f.Proof.Delivery!.MessageId));
        Assert.Equal("sign_in_dismissed", f.Core.Db.QueryFirst("SELECT reason FROM engine_message WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", f.Proof.Delivery.MessageId)));
        Assert.Equal("dismissed", f.Core.State("engine_needs_me", need)); Assert.Equal(1, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'", r => r.Long(0), default));
    }

    [Fact]
    public async Task Recovered_claim_attempt_and_original_message_attempt_remain_distinct()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false);
        Assert.True(f.Core.Apply(new RecoverEffect(), f.Effect.Id, 1).Applied); var reclaimed = f.Core.ClaimEffect(f.Effect.Id); Assert.Equal(2, reclaimed.Attempt);
        f.Proof = f.Proof with { Delivery = f.Proof.Delivery! with { ClaimAttempt = 2 } }; Assert.True(f.Apply().Applied);
        Assert.True(f.Core.Complete(reclaimed, Receipt(f)).Applied); var capsule = Data(f).AuthRefusal;
        Assert.NotNull(capsule); Assert.Equal(1, capsule.MessageAttempt); Assert.Equal(2, capsule.Blocker.OriginalClaimAttempt);
    }

    [Fact]
    public async Task Capsule_failure_rolls_back_original_completion_need_message_receipt_and_exact_retry()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); Assert.True(f.Apply().Applied); var context = f.Core.Context(f.Effect.Id, 1); var receipt = Receipt(f);
        f.Core.Db.Writer.Write("test.refusal-capsule-fault", s => s.Execute("CREATE TRIGGER fail_refusal_capsule BEFORE UPDATE OF receipt_json ON engine_outbox WHEN json_type(NEW.receipt_json,'$.data.authRefusal')='object' BEGIN SELECT RAISE(ABORT,'capsule fault'); END"));
        Assert.Throws<SqliteException>(() => f.Core.Db.Writer.Write("test.refusal-completion-fault", s => f.Core.Commands.Apply(s, context, new CompleteEffect(receipt))));
        f.AssertReserved(); Assert.Equal("inflight", f.Core.State("engine_outbox", f.Effect.Id)); Assert.Equal("open", f.Core.State("engine_needs_me", Blocker(f).NeedId));
        f.Core.Db.Writer.Write("test.refusal-capsule-unfault", s => s.Execute("DROP TRIGGER fail_refusal_capsule"));
        Assert.True(f.Core.Db.Writer.Write("test.refusal-completion-retry", s => f.Core.Commands.Apply(s, context, new CompleteEffect(receipt))).Applied); Assert.NotNull(Data(f).AuthRefusal);
    }

    [Theory]
    [InlineData("missing-need")]
    [InlineData("text")]
    [InlineData("version")]
    [InlineData("claim")]
    [InlineData("effect")]
    [InlineData("lineage")]
    public async Task Missing_or_corrupt_exact_binding_cannot_be_completed_with_fabricated_lineage(string change)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); Assert.True(f.Apply().Applied); var blocker = Blocker(f);
        f.Core.Db.Writer.Write("test.corrupt-refusal-binding", s => s.Execute(change switch
        {
            "missing-need" => "DELETE FROM engine_needs_me WHERE id=$need",
            "text" => "UPDATE engine_needs_me SET text='unrelated confirmation' WHERE id=$need",
            "version" => "UPDATE engine_needs_me SET version=0 WHERE id=$need",
            "claim" => "UPDATE rows SET payload=json_set(payload,'$.authBlocker.originalClaimAttempt',99) WHERE kind='user' AND session_id=$session",
            "effect" => "UPDATE rows SET payload=json_set(payload,'$.authBlocker.effectId','another-effect') WHERE kind='user' AND session_id=$session",
            _ => """UPDATE engine_needs_me SET state='answered',answer_json='"signed_in"',answered_by='sign-in/missing-operation',version=version+1 WHERE id=$need"""
        }, new SqlArg("$need", blocker.NeedId), new("$session", f.Core.Session.SessionId)));
        Assert.Throws<CallFailed>(() => f.Core.Complete(f.Effect, Receipt(f))); Assert.Equal("inflight", f.Core.State("engine_outbox", f.Effect.Id));
        Assert.Equal(1, f.Core.Number("SELECT in_flight FROM engine_message WHERE id=$id", f.Proof.Delivery!.MessageId));
    }

    [Fact]
    public async Task A_different_user_PK_cannot_supply_the_original_refusal_reference()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); Assert.True(f.Apply().Applied);
        Assert.Throws<CallFailed>(() => f.Core.Complete(f.Effect, Receipt(f, wrongUser: true))); f.AssertReserved();
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("unknown")]
    [InlineData("positive")]
    public async Task Other_true_receipts_never_gain_an_auth_refusal_capsule(string kind)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); Assert.True(f.Apply().Applied);
        var receipt = kind == "unknown" ? new EffectReceipt(EffectDisposition.Unknown, JsonSerializer.SerializeToElement((string?)null), "delivery_unknown") :
            new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new MessageEffectData(kind == "positive" ? "next_turn" : "queued", kind == "positive" ? "actual-provider" : null, kind == "positive" ? null : "busy", kind == "positive" ? Reference(f) : null), EngineJson.Default.MessageEffectData), null);
        Assert.True(f.Core.Complete(f.Effect, receipt).Applied);
        var saved = f.Core.Db.QueryFirst("SELECT receipt_json FROM engine_outbox WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", f.Effect.Id))!;
        using var json = JsonDocument.Parse(saved); Assert.False(json.RootElement.GetProperty("data").ValueKind == JsonValueKind.Object && json.RootElement.GetProperty("data").TryGetProperty("authRefusal", out _));
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("state")]
    [InlineData("provider")]
    public async Task A_callback_cannot_select_recovery_for_an_open_need_or_arbitrary_outcome(string change)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); Assert.True(f.Apply().Applied);
        var commands = new EngineCommands(f.Core.Runtime.Machine, new(() => 5, () => 10), projectCompletion: (_, _, _, _, _) => new MessageOutcome(change == "state" ? "failed" : "queued", change == "provider" ? "invented-provider" : null, change == "reason" ? "arbitrary" : "not_delivered"));
        Assert.Throws<CallFailed>(() => f.Core.Db.Writer.Write("test.unchecked-negative-projection", s => commands.Apply(s, f.Core.Context(f.Effect.Id, 1), new CompleteEffect(Receipt(f))))); f.AssertReserved();
    }

    private static AuthSendBlocker Blocker(AuthBlockerTests.Fixture f) => f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.AuthBlocker!;
    private static UserRowReference Reference(AuthBlockerTests.Fixture f) => new(f.Proof.Delivery!.UserRowSeq, f.Proof.Delivery.UserRunId, f.Proof.Epoch);
    private static EffectReceipt Receipt(AuthBlockerTests.Fixture f, bool wrongUser = false) => new(EffectDisposition.Confirmed,
        JsonSerializer.SerializeToElement(new MessageEffectData("queued", null, "auth_required", Reference(f) with { Seq = f.Proof.Delivery!.UserRowSeq + (wrongUser ? 1 : 0) }), EngineJson.Default.MessageEffectData), null);
    private static MessageEffectData Data(AuthBlockerTests.Fixture f) => JsonSerializer.Deserialize(f.Core.Db.QueryFirst("SELECT receipt_json FROM engine_outbox WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", f.Effect.Id))!, EngineJson.Default.EffectReceipt)!.Data.Deserialize(EngineJson.Default.MessageEffectData)!;
    private static void AssertOriginal(AuthBlockerTests.Fixture f, MessageEffectData data)
    {
        Assert.Equal("queued", data.State); Assert.Null(data.ProviderTurnId); Assert.Equal("auth_required", data.Reason); Assert.Equal(Reference(f), data.UserRow); Assert.Null(data.RetryExecution);
        Assert.Null(f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.Acknowledgment);
    }
    private static void SignedIn(AuthBlockerTests.Fixture f)
    {
        var context = f.Core.Context("operation", 0) with { Actor = new(ActorKind.Window, "owner"), Epoch = 0 };
        Assert.True(f.Core.Db.Writer.Write("test.bound-login-start", s => f.Commands.Apply(s, context, new StartSignIn("claude", "", null, Blocker(f).NeedId))).Applied);
        f.Core.ClaimEffect("begin_sign_in/operation"); var binding = new SignInAttemptBinding("operation", "claude/", "begin_sign_in/operation", 1);
        foreach (var step in new[] { ("finishing", 1L), ("signed_in", 2L) }) Assert.True(f.Core.Db.Writer.Write("test.bound-login-progress", s => f.Commands.Apply(s, f.Core.Context("operation", step.Item2) with { Actor = new(ActorKind.System, "sign_in.effects"), Epoch = 0 }, new SignInProgress(step.Item1, null, binding))).Applied);
    }
}
