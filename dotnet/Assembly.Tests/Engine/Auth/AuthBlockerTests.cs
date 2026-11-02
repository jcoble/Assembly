using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Tests.Engine.Delivery;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthBlockerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_original_claim_opens_one_auth_need_before_terminal_without_releasing_or_acknowledging_the_send(bool failedOpen)
    {
        await using var f = await Fixture.Create(failedOpen);
        var result = f.Apply(); Assert.True(result.Applied, result.Error);
        f.AssertBlocked(); Assert.Equal(result, f.Apply()); f.AssertBlocked();
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id=$id", f.Proof.BlockCommandId.ToString("N")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_receipt_replay_precedes_later_target_removal_and_holder_epoch_changes(bool failedOpen)
    {
        await using var f = await Fixture.Create(failedOpen);
        var first = f.Apply(); Assert.True(first.Applied, first.Error);
        f.Configured = false;
        f.Core.Db.Writer.Write("test.changed-after-block", s => s.Execute("UPDATE engine_session SET holder='remote',epoch=epoch+1 WHERE id=$id", new SqlArg("$id", f.Core.Session.SessionId)));
        Assert.Equal(first, f.Apply());
        var changed = f.Apply(f.Proof with { ChatRunId = "another-execution" });
        Assert.False(changed.Applied); Assert.Equal("command_id_conflict", changed.Error);
    }

    [Theory]
    [InlineData("v1-null")]
    [InlineData("v1-stage")]
    [InlineData("v2-acp")]
    [InlineData("v2-provider")]
    [InlineData("v2-stage")]
    [InlineData("unknown-version")]
    [InlineData("empty-observation")]
    [InlineData("same-command")]
    [InlineData("wrong-claim")]
    [InlineData("wrong-user")]
    [InlineData("wrong-client")]
    [InlineData("wrong-run")]
    [InlineData("wrong-epoch")]
    public async Task Mixed_copied_or_malformed_proof_cannot_open_a_need_or_mutate_the_reserved_message(string change)
    {
        await using var f = await Fixture.Create(change.StartsWith("v2-", StringComparison.Ordinal));
        var p = f.Proof;
        p = change switch
        {
            "v1-null" => p with { AcpSessionId = null },
            "v1-stage" => p with { OpenFailure = AuthOpenFailureStage.NewSession },
            "v2-acp" => p with { AcpSessionId = "borrowed-acp" },
            "v2-provider" => p with { ProviderMessageId = "borrowed-insertion" },
            "v2-stage" => p with { OpenFailure = (AuthOpenFailureStage)99 },
            "unknown-version" => p with { Version = 99 },
            "empty-observation" => p with { ObservationId = Guid.Empty },
            "same-command" => p with { ObservationId = p.BlockCommandId },
            "wrong-claim" => p with { Delivery = p.Delivery! with { ClaimAttempt = p.Delivery.ClaimAttempt + 1 } },
            "wrong-user" => p with { Delivery = p.Delivery! with { UserRowSeq = p.Delivery.UserRowSeq + 1 } },
            "wrong-client" => p with { ClientId = "another-client" },
            "wrong-run" => p with { ChatRunId = "another-execution" },
            _ => p with { Epoch = p.Epoch + 1 }
        };
        Assert.False(f.Apply(p).Applied); f.AssertUnblocked();
    }

    [Theory]
    [InlineData("window")]
    [InlineData("system-other")]
    public async Task Only_the_fixed_adapter_actor_can_report_this_actual_boundary(string actor)
    {
        await using var f = await Fixture.Create(false);
        var context = f.Context() with { Actor = actor == "window" ? new(ActorKind.Window, "owner") : new(ActorKind.System, "other") };
        Assert.False(f.Core.Db.Writer.Write("test.wrong-block-actor", s => f.Commands.Apply(s, context, new BlockSignInNeeded(f.Core.Session.SessionId, f.Proof))).Applied);
        f.AssertUnblocked();
    }

    [Fact]
    public async Task Need_write_failure_rolls_back_auth_negative_user_blocker_receipt_and_publication_before_exact_retry()
    {
        await using var f = await Fixture.Create(false);
        f.Core.Db.Writer.Write("test.block-fault", s => s.Execute("CREATE TRIGGER block_auth_need BEFORE INSERT ON engine_needs_me BEGIN SELECT RAISE(ABORT,'auth need fault'); END"));
        Assert.Throws<SqliteException>(() => f.Apply()); f.AssertUnblocked();
        f.Core.Db.Writer.Write("test.unfault-block", s => s.Execute("DROP TRIGGER block_auth_need"));
        Assert.True(f.Apply().Applied); f.AssertBlocked();
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal AdapterDeliveryFixture Core { get; } = new();
        internal bool Configured { get; set; } = true;
        internal EngineCommands Commands { get; }
        internal RowConverter Converter { get; } = new();
        internal Effect Effect { get; private set; } = null!;
        internal AuthTerminalProof Proof { get; set; } = null!;
        private long _originalEvents;
        private Fixture() => Commands = new(Core.Runtime.Machine, new(() => 5, () => 10), isConfiguredProvider: (provider, account) => Configured && provider == "claude" && account == "");
        internal static Task<Fixture> Create(bool failedOpen)
        {
            var f = new Fixture();
            f.Effect = f.Core.Claim(f.Core.Queue("exact\0body", "original-client", refs: ["b", "a"]));
            var intent = f.Effect.Payload.Deserialize(EngineJson.Default.DeliverMessage)!;
            f.Core.Rows.Commit(f.Core.Session.SessionId, f.Converter.Apply(new TurnStarted(1000, "R1", intent.Text, intent.ClientId)));
            var user = f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, intent.ClientId)!;
            f.Core.Db.Writer.Write("test.actual-open-identity", s => s.Execute("UPDATE engine_session SET provider_session_id=$acp WHERE id=$id",
                new SqlArg("$acp", failedOpen ? null : "actual-acp"), new("$id", f.Core.Session.SessionId)));
            f.Proof = new(failedOpen ? 2 : 1, Guid.NewGuid(), Guid.NewGuid(), "R1", failedOpen ? null : "actual-acp", intent.HeldEpoch!.Value,
                intent.ClientId, null, new(intent.MessageId, f.Effect.Id, f.Effect.Attempt, intent.ClientId, user.Seq, user.RunId!),
                failedOpen ? AuthOpenFailureStage.Initialize : null);
            f._originalEvents = f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_event", r => r.Long(0), default);
            return Task.FromResult(f);
        }
        internal CommandContext Context() => new(Proof.BlockCommandId, Core.Runtime.Machine, new(ActorKind.System, "adapter"),
            "auth-blocker/" + Proof.BlockCommandId.ToString("N"), 0, Proof.Epoch, 1000);
        internal CommandResult Apply(AuthTerminalProof? proof = null) => Core.Db.Writer.Write("test.auth-block", s =>
            Commands.Apply(s, Context(), new BlockSignInNeeded(Core.Session.SessionId, proof ?? Proof)));
        internal void AssertUnblocked()
        {
            Assert.Equal(0, Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
            Assert.Null(Core.Rows.UserDeliveryOfClient(Core.Session.SessionId, Proof.ClientId!)!.AuthBlocker);
            Assert.Equal(_originalEvents, Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_event", r => r.Long(0), default));
            // A valid refused command may retain its ordinary refusal receipt; no applied negative observation exists.
            Assert.Equal(0, Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_provider", r => r.Long(0), default));
            AssertReserved();
        }
        internal void AssertBlocked()
        {
            var need = Core.Db.QueryFirst("SELECT id,version,text,kind,provider_request_id,chat_row_id,state FROM engine_needs_me", r =>
                (Id: r.Text(0), Version: r.Long(1), Text: r.Text(2), Kind: r.Text(3), Request: r.IsNull(4), Chat: r.IsNull(5), State: r.Text(6)), default);
            Assert.StartsWith("sign-in/" + Core.Session.SessionId + "/", need.Id);
            Assert.Equal("claude needs you to sign in on " + Core.Runtime.Machine + ".", need.Text);
            Assert.Equal("confirmation", need.Kind); Assert.Equal("open", need.State); Assert.True(need.Request); Assert.True(need.Chat);
            var blocker = Core.Rows.UserDeliveryOfClient(Core.Session.SessionId, Proof.ClientId!)!.AuthBlocker;
            Assert.Equal(new AuthSendBlocker(Effect.Id, Effect.Attempt, Proof.ObservationId, need.Id, need.Version, Proof.BlockCommandId), blocker);
            Assert.Equal("needs_sign_in", Core.Db.QueryFirst("SELECT auth_state FROM engine_provider WHERE id='claude/'", r => r.Text(0), default));
            AssertReserved();
        }
        internal void AssertReserved(long rows = 2)
        {
            Assert.Equal("queued", Core.State("engine_message", Proof.Delivery!.MessageId)); Assert.Equal("inflight", Core.State("engine_outbox", Effect.Id));
            Assert.Equal(1, Core.Number("SELECT in_flight FROM engine_message WHERE id=$id", Proof.Delivery.MessageId));
            Assert.Null(Core.Rows.UserDeliveryOfClient(Core.Session.SessionId, Proof.ClientId!)!.Acknowledgment);
            Assert.Equal(rows, Core.Rows.LastSeq(Core.Session.SessionId));
        }
        public ValueTask DisposeAsync() => Core.DisposeAsync();
    }
}
