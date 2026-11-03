using Assembly.Engine;
using Assembly.Application.Common.Database;
using Assembly.Engine.Handoffs;

namespace Assembly.Tests.Engine;

public sealed class HandoffRequestEligibilityTests
{
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
