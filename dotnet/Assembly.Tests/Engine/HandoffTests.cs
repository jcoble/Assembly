using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Engine.Effects;
using Assembly.Tests.Engine.Handoffs;

namespace Assembly.Tests.Engine;

public sealed class HandoffTests
{
    private static readonly EngineActor Target = new(ActorKind.System, "target");

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

    public static IEnumerable<object[]> Edges()
    {
        foreach (var from in new[] { "requested", "accepted", "resumed", "failed" })
            foreach (var to in new[] { "requested", "accepted", "resumed", "failed", "unknown" })
                yield return [from, to, (from, to) is ("requested", "accepted" or "failed") or ("accepted", "resumed" or "failed")];
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
}
