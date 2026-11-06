using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Runs;

namespace Assembly.Tests.Engine.Workflows.Scheduling;

public sealed class WorkflowDependencyTests
{
    [Theory]
    [InlineData("running", true, false, false, false)]
    [InlineData("waiting", true, false, false, false)]
    [InlineData("failed", true, false, false, true)]
    [InlineData("failed", false, false, false, false)]
    [InlineData("succeeded", false, false, false, true)]
    [InlineData("skipped", false, true, true, true)]
    [InlineData("skipped", false, true, false, false)]
    [InlineData("skipped", false, false, true, false)]
    public void Dependencies_must_settle_and_skips_require_checked_workflow_rule(string dependencyState, bool optional,
        bool ruleSkippable, bool hasRuleReason, bool expected)
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedRun(); f.SeedStep();
        f.SeedSession("worker", parentId: "s", runId: "r"); f.SeedStep("dependency", state: dependencyState, ruleSkippable: ruleSkippable);
        f.Db.Writer.Write("test.dependencies", s => s.Execute("""
            INSERT INTO engine_step_dependency(step_id,dependency_id,optional) VALUES('step','dependency',$optional);
            UPDATE engine_step SET outcome_reason=$reason WHERE id='dependency';
            """, new("$optional", optional ? 1 : 0), new("$reason", hasRuleReason ? "workflow condition false" : null)));
        var result = f.Apply(new StartStep("worker"), "step", actor: new(ActorKind.Session, "s"));
        Assert.Equal(expected, result.Applied);
        Assert.Equal(expected ? "running" : "pending", f.State("engine_step", "step"));
        Assert.Equal(expected ? 1 : 0, f.Count("engine_step_attempt"));
        if (!expected) Assert.Equal("dependency_not_ready", result.Error);
    }
}
