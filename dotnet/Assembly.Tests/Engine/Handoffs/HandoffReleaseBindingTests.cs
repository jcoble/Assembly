using Assembly.Engine;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Handoffs;

namespace Assembly.Tests.Engine;

public sealed class HandoffReleaseBindingTests
{
    [Theory]
    [InlineData("effect")]
    [InlineData("attempt")]
    [InlineData("epoch")]
    [InlineData("session")]
    [InlineData("public-acp")]
    [InlineData("operation")]
    public void Claimed_release_refuses_mismatched_or_missing_completion_identity(string changed)
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        f.Db.Writer.Write("test.public-acp", scope => scope.Execute("UPDATE engine_session SET provider_session_id='public-acp' WHERE id='s'"));
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "H", epoch: 7).Applied);
        var effect = f.Text("SELECT id FROM engine_outbox")!;
        var actor = new EngineActor(ActorKind.System, "engine.worker");
        Assert.True(f.Apply(new ClaimEffect(), effect, epoch: 7, actor: actor).Applied);
        var identity = new HandoffIdentity("H", f.Machine, "s", "target", 7, 8);
        var data = new HandoffReleaseEffectData(identity, "s", 7, "public-acp", "captured-operation", effect, 1);
        data = changed switch
        {
            "effect" => data with { ReleaseEffectId = "other" },
            "attempt" => data with { ReleaseAttempt = 2 },
            "epoch" => data with { Epoch = 8 },
            "session" => data with { SessionId = "other" },
            "public-acp" => data with { ProviderSessionId = "other" },
            _ => data with { AdapterOperationId = "" },
        };
        var receipt = new EffectReceipt(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(data, EngineJson.Default.HandoffReleaseEffectData), null);
        Assert.Equal("bad_receipt", f.Apply(new CompleteEffect(receipt), effect, version: 1, epoch: 7, actor: actor).Error);
        Assert.Equal("inflight", f.State("engine_outbox", effect));
        using var snapshot = f.Db.OpenSnapshot(default);
        Assert.Null(new EngineCommands(f.Machine, f.Limits).ReadHandoffRelease(snapshot, identity));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("dead")]
    public void Terminal_outbox_state_alone_never_becomes_positive_release_evidence(string state)
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "H", epoch: 7).Applied);
        f.Db.Writer.Write("test.terminal-without-proof", scope => scope.Execute("UPDATE engine_outbox SET state=$state", new SqlArg("$state", state)));
        var identity = new HandoffIdentity("H", f.Machine, "s", "target", 7, 8);
        var ack = f.Db.Writer.Write("test.no-positive-release", scope => new EngineCommands(f.Machine, f.Limits)
            .ApplyHandoffPhase(scope, new(identity, HandoffPhase.Accept, 1, null, null), 2000));
        Assert.Equal(HandoffGate.Unknown, ack.Gate); Assert.Null(ack.Receipt);
        Assert.Equal("requested", f.State("engine_handoff", "H"));
    }
}
