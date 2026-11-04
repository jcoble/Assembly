using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Engine.Effects;

namespace Assembly.Tests.Engine;

public sealed class HandoffTests
{
    private static readonly EngineActor Target = new(ActorKind.System, "target");

    [Theory]
    [InlineData("resumed", 8L, "moved")]
    [InlineData("failed", 7L, "idle")]
    public void Only_resume_moves_epoch(string outcome, long epoch, string state)
    {
        using var f = new EngineFixture(); f.SeedSession("s", epoch: 7);
        using var target = new EngineFixture();
        Native(f);
        var identity = new HandoffIdentity("h", f.Machine, "s", target.Machine, 7, 8);
        Assert.True(f.Apply(new RequestHandoff("s", target.Machine), "h", epoch: 7).Applied);
        CompleteRelease(f, identity);
        Assert.True(Phase(f, identity, HandoffPhase.Accept).Receipt!.Applied);
        Assert.Equal(7, f.Number("SELECT epoch FROM engine_session WHERE id='s'"));
        Assert.Equal("local", f.Text("SELECT holder FROM engine_session WHERE id='s'"));
        var evidence = outcome == "failed" ? Seal(target, identity) : null;
        Assert.True(Phase(f, identity, outcome == "failed" ? HandoffPhase.Failed : HandoffPhase.Resumed,
            outcome == "failed" ? "refused" : null, evidence).Receipt?.Applied == true);
        Assert.Equal(epoch, f.Number("SELECT epoch FROM engine_session WHERE id='s'"));
        Assert.Equal(state, f.State("engine_session", "s"));
        Assert.Equal(outcome == "resumed" ? target.Machine : "local", f.Text("SELECT holder FROM engine_session"));
        Assert.Null(f.Text("SELECT reserved_epoch FROM engine_session"));
        Assert.Equal(outcome == "failed" ? "refused" : null, f.Text("SELECT reason FROM engine_handoff"));
        Assert.Equal(outcome, f.State("engine_handoff", "h"));
        Assert.Equal(2, f.Number("SELECT version FROM engine_session"));
        Assert.Equal(3, f.Number("SELECT version FROM engine_handoff"));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void Fence_returns_reserved_epoch(long epoch)
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        var id = Guid.NewGuid();
        var requested = f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7, commandId: id);
        Assert.True(requested.Applied); Assert.Equal(8, requested.Epoch);
        f.Reopen();
        Assert.Equal(requested, f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7, commandId: id));
        var events = f.Count("engine_event"); var effects = f.Count("engine_outbox");
        foreach (SessionCommand command in new SessionCommand[] { new StopSession(), new SetSessionState("idle", null, null) })
        {
            var result = f.Apply(command, "s", version: 1, epoch: epoch, actor: Target);
            Assert.Equal("handing_off", result.Error); Assert.Equal(8, result.Epoch);
        }
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(effects, f.Count("engine_outbox"));
        Assert.Equal("handing_off", f.State("engine_session", "s"));
        Assert.Equal(8, f.Number("SELECT reserved_epoch FROM engine_session"));
        Assert.Equal("release", f.Text("SELECT kind FROM engine_outbox"));
        var payload = f.Text("SELECT payload_json FROM engine_outbox")!;
        var release = JsonSerializer.Deserialize(payload, EngineJson.Default.HandoffReleaseEffect)!;
        Assert.Equal(new HandoffIdentity("h", f.Machine, "s", "target", 7, 8), release.Identity);
        Assert.Equal("codex", release.Provider);
        Assert.Equal(new RequestHandoff("s", "target"), JsonSerializer.Deserialize(
            JsonSerializer.Serialize<EngineCommand>(new RequestHandoff("s", "target"), EngineJson.Default.EngineCommand),
            EngineJson.Default.EngineCommand));
        Assert.Equal(new HandoffOutcome("accepted", 8, null), JsonSerializer.Deserialize(
            JsonSerializer.Serialize<EngineCommand>(new HandoffOutcome("accepted", 8, null), EngineJson.Default.EngineCommand),
            EngineJson.Default.EngineCommand));
    }

    [Fact]
    public void Inflight_drains_before_transfer()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        f.Db.Writer.Write("test.native", scope => scope.Execute("UPDATE engine_session SET provider_session_id='native' WHERE id='s'"));
        var sent = f.Apply(new SendMessage(null, "s", "hello", "prompt"), "m", epoch: 7);
        Assert.True(sent.Applied);
        f.Db.Writer.Write("test.inflight", s => s.Execute("UPDATE engine_outbox SET state='inflight' WHERE kind='send'"));
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        var identity = new HandoffIdentity("h", f.Machine, "s", "target", 7, 8);
        var before = f.Count("engine_command_receipt");
        var blocked = Phase(f, identity, HandoffPhase.Accept);
        Assert.Equal(HandoffGate.Waiting, blocked.Gate); Assert.Equal("unavailable", blocked.Reason);
        Assert.Null(blocked.Receipt); Assert.Equal(before, f.Count("engine_command_receipt"));
        Assert.Equal("requested", f.State("engine_handoff", "h"));
        Assert.Equal("inflight", f.Text("SELECT state FROM engine_outbox WHERE kind='send'"));
        Assert.True(f.Apply(new MessageOutcome("delivered_to_turn", "turn", null), "m", version: sent.Version, epoch: 7, actor: Target).Applied);
        f.Db.Writer.Write("test.drain", s => s.Execute("UPDATE engine_outbox SET state='done' WHERE kind='send'"));
        Assert.Equal(HandoffGate.Waiting, Phase(f, identity, HandoffPhase.Accept).Gate);
        CompleteRelease(f, identity);
        Assert.True(Phase(f, identity, HandoffPhase.Accept).Receipt!.Applied);
    }

    [Fact]
    public void Earlier_and_current_release_must_both_confirm_before_accept()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7); Native(f);
        var first = new HandoffIdentity("h", f.Machine, "s", "target", 7, 8);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        var release = f.Text("SELECT id FROM engine_outbox")!;
        Assert.True(f.Apply(new ClaimEffect(), release, epoch: 7, actor: Target).Applied);
        Assert.True(Phase(f, first, HandoffPhase.Failed, "refused").Receipt!.Applied);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h2", version: 2, epoch: 7).Applied);
        var second = first with { HandoffId = "h2" };
        Assert.Equal(HandoffGate.Waiting, Phase(f, second, HandoffPhase.Accept).Gate);
        CompleteRelease(f, first);
        Assert.Equal(HandoffGate.Waiting, Phase(f, second, HandoffPhase.Accept).Gate);
        CompleteRelease(f, second);
        Assert.True(Phase(f, second, HandoffPhase.Accept).Receipt!.Applied);
    }

    [Fact]
    public void Wrong_outcome_identity_is_refused()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7); Native(f);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        var identity = new HandoffIdentity("h", f.Machine, "s", "target", 7, 8);
        // P4 Task10 forbids every new generic outcome, including the exact trusted phase context.
        foreach (var actor in new[] { Target, new EngineActor(ActorKind.System, "handoff"), new(ActorKind.System, "local"),
            new(ActorKind.System, "machine:target"), new(ActorKind.Window, "target") })
            foreach (var (contextEpoch, nextEpoch) in new[] { (7L, 8L), (8L, 9L), (8L, 8L) })
                Assert.Equal("forbidden", f.Apply(new HandoffOutcome("accepted", nextEpoch, null), "h", 1, contextEpoch, actor).Error);
        foreach (var changed in new[] { identity with { TargetMachine = "other" }, identity with { SourceSessionId = "other" },
            identity with { OldEpoch = 8, NextEpoch = 9 } })
        {
            var rejected = Phase(f, changed, HandoffPhase.Accept);
            Assert.Equal(HandoffGate.Unknown, rejected.Gate); Assert.Null(rejected.Receipt);
        }
        var stale = f.Db.Writer.Write("test.stale-observation", scope => new EngineCommands(f.Machine, f.Limits)
            .ApplyHandoffPhase(scope, new(identity, HandoffPhase.Accept, 0, null, null), 1000));
        Assert.Equal(HandoffGate.Advanced, stale.Gate); Assert.Null(stale.Receipt);
        var duplicate = f.Apply(new RequestHandoff("s", "other"), "h2", version: 1, epoch: 7);
        Assert.Equal("handing_off", duplicate.Error); Assert.Equal(8, duplicate.Epoch);
        Assert.Equal(1, f.Count("engine_handoff"));
        CompleteRelease(f, identity);
        Assert.True(Phase(f, identity, HandoffPhase.Accept).Receipt!.Applied);
        foreach (var outcome in new[] { "resumed", "failed" })
            Assert.Equal("forbidden", f.Apply(new HandoffOutcome(outcome, 8, "refused"), "h", 2, 8,
                new(ActorKind.System, "machine:target")).Error);
        Assert.Equal("accepted", f.State("engine_handoff", "h"));
        Assert.Equal(7, f.Number("SELECT epoch FROM engine_session WHERE id='s'"));
        Assert.Equal(8, f.Number("SELECT reserved_epoch FROM engine_session WHERE id='s'"));
    }

    public static IEnumerable<object[]> Edges()
    {
        foreach (var from in new[] { "requested", "accepted", "resumed", "failed" })
            foreach (var to in new[] { "requested", "accepted", "resumed", "failed", "unknown" })
                yield return [from, to, (from, to) is ("requested", "accepted" or "failed") or ("accepted", "resumed" or "failed")];
    }

    [Theory]
    [MemberData(nameof(Edges))]
    public void Allowed_and_invalid_edges(string from, string to, bool allowed)
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7); Native(f);
        using var target = new EngineFixture();
        var identity = new HandoffIdentity("h", f.Machine, "s", target.Machine, 7, 8);
        Assert.True(f.Apply(new RequestHandoff("s", target.Machine), "h", epoch: 7).Applied);
        long version = 1;
        if (from is "accepted" or "resumed")
        {
            CompleteRelease(f, identity);
            Assert.True(Phase(f, identity, HandoffPhase.Accept).Receipt!.Applied); version++;
            if (from == "resumed")
            {
                // The real target native-operation producer is required; display idle cannot stand in for it.
                Assert.True(Phase(f, identity, HandoffPhase.Resumed).Receipt?.Applied == true); version++;
            }
        }
        if (from == "failed")
        {
            Assert.True(Phase(f, identity, HandoffPhase.Failed, "refused").Receipt!.Applied); version++;
        }
        var events = f.Count("engine_event"); var effects = f.Count("engine_outbox");
        if (to == "accepted" && from == "requested") CompleteRelease(f, identity);
        var phase = to switch
        {
            "accepted" => HandoffPhase.Accept,
            "resumed" => HandoffPhase.Resumed,
            "failed" => HandoffPhase.Failed,
            _ => (HandoffPhase)999
        };
        var evidence = to == "failed" && from == "accepted" ? Seal(target, identity) : null;
        var ack = Phase(f, identity, phase, to == "failed" ? "refused" : null, evidence);
        Assert.Equal(allowed ? to : from, f.State("engine_handoff", "h"));
        Assert.Equal(allowed ? version + 1 : version, f.Number("SELECT version FROM engine_handoff WHERE id='h'"));
        if (allowed) Assert.True(ack.Receipt?.Applied == true);
        else { Assert.Equal(events, f.Count("engine_event")); Assert.Equal(effects, f.Count("engine_outbox")); }
    }

    [Fact]
    public void Old_holder_can_fail_only_before_accept_and_failure_is_durable()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        var identity = new HandoffIdentity("h", f.Machine, "s", "target", 7, 8);
        var failed = f.Db.Writer.Write("test.requested-failure", scope => new EngineCommands(f.Machine, f.Limits)
            .ApplyHandoffPhase(scope, new(identity, HandoffPhase.Failed, 1, "cannot map", null), 1000));
        Assert.True(failed.Receipt!.Applied);
        f.Reopen();
        Assert.Equal("idle", f.State("engine_session", "s"));
        Assert.Equal(7, f.Number("SELECT epoch FROM engine_session"));
        Assert.Equal("local", f.Text("SELECT holder FROM engine_session"));
        Assert.Equal("cannot map", f.Text("SELECT reason FROM engine_handoff"));
        Assert.True(f.Apply(new StopSession(), "s", version: 2, epoch: 7).Applied);
    }

    private static void Native(EngineFixture f) => f.Db.Writer.Write("test.native", scope =>
        scope.Execute("UPDATE engine_session SET provider_session_id='native' WHERE id='s'"));

    internal static HandoffTargetEvidence Seal(EngineFixture target, HandoffIdentity identity) => target.Db.Writer.Write("test.actual-seal", scope =>
        new EngineCommands(target.Machine, target.Limits).SealHandoffTarget(scope, new(identity, "refused"), 1000)).Target!;

    internal static HandoffAck Phase(EngineFixture f, HandoffIdentity identity, HandoffPhase phase, string? reason = null,
        HandoffTargetEvidence? target = null) => f.Db.Writer.Write("test.trusted-phase", scope =>
            new EngineCommands(f.Machine, f.Limits).ApplyHandoffPhase(scope,
                new(identity, phase, scope.QueryFirst("SELECT version FROM engine_handoff WHERE id=$id", row => row.Long(0), new SqlArg("$id", identity.HandoffId)), reason, target), 1000));

    internal static void CompleteRelease(EngineFixture f, HandoffIdentity identity)
    {
        var id = f.Text("SELECT id FROM engine_outbox WHERE kind='release' AND json_extract(payload_json,'$.identity.handoffId')=$id",
            new SqlArg("$id", identity.HandoffId))!;
        var actor = new EngineActor(ActorKind.System, "worker");
        if (f.State("engine_outbox", id) == "pending")
            Assert.True(f.Apply(new ClaimEffect(), id, epoch: identity.OldEpoch, actor: actor).Applied);
        var intent = JsonSerializer.Deserialize(f.Text("SELECT payload_json FROM engine_outbox WHERE id=$id", new SqlArg("$id", id))!,
            EngineJson.Default.HandoffReleaseEffect)!;
        var data = new HandoffReleaseEffectData(identity, identity.SourceSessionId, identity.OldEpoch,
            intent.ProviderSessionId!, "controlled-disposed-operation", id, 1);
        var receipt = new EffectReceipt(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(data, EngineJson.Default.HandoffReleaseEffectData), null);
        var completed = f.Apply(new CompleteEffect(receipt), id, 1, identity.OldEpoch, actor);
        Assert.True(completed.Applied, completed.Error);
    }

    [Fact]
    public void Request_checks_session_version_epoch_arguments_and_actor()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.Equal("version_conflict", f.Apply(new RequestHandoff("s", "target"), "h", version: 1, epoch: 7).Error);
        Assert.Equal("stale_epoch", f.Apply(new RequestHandoff("s", "target"), "h", epoch: 6).Error);
        foreach (var request in new[] { new RequestHandoff("missing", "target"), new("s", " "), new("s", "local") })
            Assert.Equal("bad_args", f.Apply(request, "h", epoch: 7).Error);
        Assert.Equal("forbidden", f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7, actor: Target).Error);
        Assert.Empty(f.Events()); Assert.Equal(0, f.Count("engine_outbox"));
    }
}
