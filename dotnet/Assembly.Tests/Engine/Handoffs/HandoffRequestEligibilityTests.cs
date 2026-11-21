using Assembly.Engine;
using Assembly.Application.Common.Database;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Sessions;
using Assembly.Engine.Effects;
using Assembly.Engine.Contracts;
using System.Text.Json;

namespace Assembly.Tests.Engine;

public sealed class HandoffRequestEligibilityTests
{
    [Theory]
    [InlineData("start", false)]
    [InlineData("start", true)]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    public void Earlier_session_effect_drains_before_handoff_can_reserve_an_epoch(string kind, bool inflight)
    {
        using var f = new EngineFixture();
        var system = new EngineActor(ActorKind.System, "adapter");
        Assert.True(f.Apply(new StartSession("codex", null, f.Folder, "Source", null, null, null), "s").Applied);
        var start = f.Text("SELECT id FROM engine_outbox WHERE kind='start'")!;
        if (kind == "stop")
        {
            Assert.True(f.Apply(new ClaimEffect(), start, actor: system).Applied);
            Assert.True(f.Apply(new CompleteEffect(new(EffectDisposition.Confirmed,
                JsonSerializer.SerializeToElement(new SessionEffectData("idle", "native", null), EngineJson.Default.SessionEffectData), null)), start, 1, actor: system).Applied);
            Assert.True(f.Apply(new StopSession(), "s", f.Number("SELECT version FROM engine_session WHERE id='s'")).Applied);
        }
        else Assert.True(f.Apply(new SetSessionState("idle", "native", null), "s", 1, actor: system).Applied);
        var effect = f.Text("SELECT id FROM engine_outbox WHERE kind=$kind", new SqlArg("$kind", kind))!;
        if (inflight) Assert.True(f.Apply(new ClaimEffect(), effect, actor: system).Applied);
        var version = f.Number("SELECT version FROM engine_session WHERE id='s'");
        Assert.Equal("busy", f.Apply(new RequestHandoff("s", "target"), "blocked", version).Error);
        Assert.Null(f.Text("SELECT reserved_epoch FROM engine_session WHERE id='s'"));
        Assert.Equal(0, f.Count("engine_handoff"));
        if (!inflight) Assert.True(f.Apply(new ClaimEffect(), effect, actor: system).Applied);
        Assert.True(f.Apply(new CompleteEffect(new(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new SessionEffectData("idle", "native", null), EngineJson.Default.SessionEffectData), null)), effect, 1, actor: system).Applied);
        Assert.Equal("done", f.State("engine_outbox", effect));
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "ready",
            f.Number("SELECT version FROM engine_session WHERE id='s'")).Applied);
    }

    [Fact]
    public void Handoff_is_refused_while_a_message_is_in_flight()
    {
        // A steer released into a running turn still waits on the agent after the turn ends.
        using var f = new EngineFixture(); f.SeedSession(state: "running", epoch: 7);
        f.Db.Writer.Write("test.can-steer", scope => scope.Execute("UPDATE engine_session SET can_steer=1 WHERE id='s'"));
        Assert.True(f.Apply(new Assembly.Engine.Messages.SendMessage(null, "s", "hello", "prompt"), "m", epoch: 7,
            actor: new(ActorKind.Window, "owner")).Applied);
        Assert.Equal("steer/s", f.Text("SELECT order_key FROM engine_outbox WHERE kind='send'"));
        f.Apply(new SetSessionState("idle", "native", null), "s", f.Number("SELECT version FROM engine_session WHERE id='s'"), epoch: 7,
            actor: new(ActorKind.System, "adapter"));
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id='m'"));
        Assert.Equal("idle", f.State("engine_session", "s"));
        var version = f.Number("SELECT version FROM engine_session WHERE id='s'");
        Assert.Equal("busy", f.Apply(new RequestHandoff("s", "target"), "H", version, epoch: 7).Error);
        Assert.Equal(0, f.Count("engine_handoff"));
        Assert.Null(f.Text("SELECT reserved_epoch FROM engine_session WHERE id='s'"));
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("holder")]
    public void Nonowning_source_cannot_mint_local_release(string changed)
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        f.Db.Writer.Write("test.foreign-source", scope => scope.Execute(
            changed == "machine" ? "UPDATE engine_session SET machine='other' WHERE id='s'"
                : "UPDATE engine_session SET holder='other' WHERE id='s'"));
        Assert.Equal("writer_elsewhere", f.Apply(new RequestHandoff("s", "target"), "H", epoch: 7).Error);
        Assert.Equal(0, f.Count("engine_handoff"));
        Assert.Equal(0, f.Count("engine_outbox"));
    }
}
