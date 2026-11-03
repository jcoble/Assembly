using Assembly.Engine.Contracts;
using Assembly.Engine.Runs;
using Assembly.Application.Common.Database;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowHandoffTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Foreign_run_reference_cannot_accept_local_coordinator_control_or_owner_loss(bool defined)
    {
        using var f = new EngineFixture();
        f.SeedSession("owner", "lost"); f.SeedRun("reference", "owner", "running");
        f.Db.Writer.Write("test.foreign-run-reference", scope => scope.Execute("""
            UPDATE engine_run SET machine='coordinator',definition_hash=$hash,owner_lost_ms=1 WHERE id='reference';
            UPDATE engine_session SET lost_ms=1 WHERE id='owner';
            """, new SqlArg("$hash", defined ? "definition-hash" : null)));
        var cancelled = f.Apply(new CancelRun(), "reference");
        Assert.False(cancelled.Applied); Assert.Equal("wrong_machine", cancelled.Error);
        var expired = f.Db.Writer.Write("test.foreign-owner-loss", scope => RunTransitions.ExpireLostOwner(scope,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "engine.worker"), "reference", 0, 0, 100_000), "reference"));
        Assert.False(expired.Applied); Assert.Equal("wrong_machine", expired.Error);
        Assert.Equal("running", f.State("engine_run", "reference"));
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Foreign_run_reference_is_not_recomputed_but_its_original_coordinator_can_recompute()
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedRun("reference", "owner", "running");
        f.Db.Writer.Write("test.foreign-run-reference", scope => scope.Execute("UPDATE engine_run SET machine='coordinator' WHERE id='reference'"));
        var events = f.Db.Writer.Write("test.foreign-recompute", scope => RunTransitions.Recompute(scope,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "engine.worker"), "reference", 0, 0, 1000), "reference"));
        Assert.Empty(events); Assert.Equal("running", f.State("engine_run", "reference"));
        var original = f.Db.Writer.Write("test.coordinator-recompute", scope => RunTransitions.Recompute(scope,
            new(Guid.NewGuid(), "coordinator", new(ActorKind.System, "engine.worker"), "reference", 0, 0, 1000), "reference"));
        Assert.Single(original); Assert.Equal("succeeded", f.State("engine_run", "reference"));
    }
}
