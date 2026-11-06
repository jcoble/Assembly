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

    [Theory]
    [InlineData("starting")]
    [InlineData("running")]
    [InlineData("needs_me")]
    [InlineData("lost")]
    public void Busy_source_cannot_allocate_handoff_or_release(string state)
    {
        using var f = new EngineFixture(); f.SeedSession(state: state, epoch: 7);
        var result = f.Apply(new RequestHandoff("s", "target"), "H", epoch: 7);
        Assert.Equal("busy", result.Error);
        Assert.Equal(state, f.State("engine_session", "s"));
        Assert.Equal(0, f.Count("engine_handoff"));
        Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Equal(0, f.Count("engine_event"));
    }

    [Fact]
    public void Child_is_not_a_top_level_handoff_source()
    {
        using var f = new EngineFixture(); f.SeedSession("parent"); f.SeedSession(parentId: "parent", epoch: 7);
        Assert.Equal("has_parent", f.Apply(new RequestHandoff("s", "target"), "H", epoch: 7).Error);
        Assert.Equal(0, f.Count("engine_handoff"));
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("running")]
    [InlineData("idle")]
    [InlineData("needs_me")]
    [InlineData("lost")]
    public void Unfinished_child_keeps_source_on_its_machine(string childState)
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7); f.SeedSession("child", state: childState, parentId: "s");
        Assert.Equal("children_running", f.Apply(new RequestHandoff("s", "target"), "H", epoch: 7).Error);
        Assert.Equal("idle", f.State("engine_session", "s"));
        Assert.Equal(0, f.Count("engine_handoff"));
    }

    [Fact]
    public void Request_preserves_physical_local_holder_and_queues_release_for_actual_source_authority()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "H", epoch: 7).Applied);
        Assert.Equal("local", f.Text("SELECT holder FROM engine_session WHERE id='s'"));
        Assert.Equal("local", f.Text("SELECT old_holder FROM engine_handoff WHERE id='H'"));
        Assert.Equal("release", f.Text("SELECT kind FROM engine_outbox"));
        var payload = System.Text.Json.JsonSerializer.Deserialize(f.Text("SELECT payload_json FROM engine_outbox")!,
            EngineJson.Default.HandoffReleaseEffect)!;
        Assert.Equal(new HandoffIdentity("H", f.Machine, "s", "target", 7, 8), payload.Identity);
        Assert.Null(payload.ProviderSessionId); // Missing public ACP identity is not invented native/capture proof.
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
