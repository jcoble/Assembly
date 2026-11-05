using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Reading;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Tests.Engine.Workflows;

/// <summary>What a stuck step's row is offered must be what the engine then does.</summary>
public sealed class WorkflowStepRecoveryTests
{
    private static EngineFixture Step(string state, string? reason, string kind = "agent", bool required = true, bool skippable = false,
        int used = 0, int max = 1, bool granted = false, string runState = "running")
    {
        var f = WorkflowControlTests.Fixture(runState); f.SeedStep(state: state, attempt: used, maxAttempts: max, required: required, ruleSkippable: skippable);
        f.Db.Writer.Write("test.step", s => s.Execute(
            "UPDATE engine_step SET kind=$kind,outcome_reason=$reason,attempt_in_generation=$used,scope_granted=$granted,required_scope=CASE WHEN $reason='scope_required' THEN 'repo:write' END",
            new SqlArg("$kind", kind), new("$reason", reason), new("$used", used), new("$granted", granted ? 1 : 0)));
        return f;
    }

    private static string[] Offered(EngineFixture f) => new EngineReader(f.Db, f.Machine).Entity("step", "step", default)!.Value
        .GetProperty("recovery").EnumerateArray().Select(action => action.GetString()!).ToArray();

    private static CommandResult Act(EngineFixture f, string action) => f.Apply(action == "retry"
        ? new RetryWorkflowStep("step") : new DecideWorkflowStep("step", action), "r", f.Number("SELECT version FROM engine_run"));

    private static readonly Dictionary<string, (Func<EngineFixture> Make, string[] Offered)> Scenarios = new()
    {
        ["failed with attempts left"] = (() => Step("failed", null, used: 1, max: 2), ["retry"]),
        ["failed with none left"] = (() => Step("failed", null, used: 2, max: 2), []),
        ["session lost"] = (() => Step("waiting", "session_lost", used: 1), ["retry"]),
        ["interrupted"] = (() => Step("waiting", "interrupted", used: 1), ["retry"]),
        ["timed out"] = (() => Step("waiting", "timed_out", used: 1), ["retry"]),
        ["waiting on its reviewers"] = (() => Step("waiting", "review_findings", used: 1), []),
        ["running"] = (() => Step("running", null, used: 1), []),
        ["optional and not started"] = (() => Step("pending", null, required: false), ["skip"]),
        ["required and not started"] = (() => Step("pending", null), []),
        ["its condition was false"] = (() => Step("pending", "condition_false", skippable: true), ["skip"]),
        ["an optional gate"] = (() => Step("pending", null, kind: "gate", required: false), []),
        ["scope confirmed"] = (() => Step("waiting", "scope_required", granted: true), ["continue"]),
        ["scope not confirmed"] = (() => Step("waiting", "scope_required"), []),
        ["the run has failed"] = (() => Step("failed", null, used: 1, max: 2, runState: "failed"), []),
        ["the run is cancelling"] = (() => Step("failed", null, used: 1, max: 2, runState: "cancelling"), []),
    };

    public static TheoryData<string> ScenarioNames => new(Scenarios.Keys);

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void A_row_is_offered_exactly_what_the_engine_then_applies(string name)
    {
        var (make, expected) = Scenarios[name];
        using (var f = make()) Assert.Equal(expected, Offered(f));
        foreach (var action in new[] { "retry", "skip", "continue" })
        {
            using var f = make();
            Assert.Equal(expected.Contains(action), Act(f, action).Applied);
        }
    }

    [Fact]
    public void A_worker_still_stopping_keeps_the_control_and_the_engine_refuses_until_it_has()
    {
        using var f = WorkflowControlTests.Fixture(); f.SeedSession("worker", state: "running", parentId: "owner", runId: "r");
        f.SeedStep(state: "waiting", sessionId: "worker", attempt: 1, maxAttempts: 2);
        f.Db.Writer.Write("test.lost", s => s.Execute("UPDATE engine_step SET kind='agent',outcome_reason='session_lost',attempt_in_generation=1"));
        Assert.Equal(["retry"], Offered(f));
        Assert.Equal("executor_quiescence_required", Act(f, "retry").Error);
        Assert.Equal("waiting", f.State("engine_step", "step"));
    }

    [Fact]
    public void Retrying_a_stuck_step_moves_it_and_stops_offering_it()
    {
        using var f = Step("waiting", "session_lost", used: 1);
        Assert.True(Act(f, "retry").Applied);
        Assert.Equal("pending", f.State("engine_step", "step"));
        Assert.Empty(Offered(f));
        Assert.Equal(1, f.Number("SELECT generation FROM engine_step"));
    }

    [Fact]
    public void A_window_sends_the_step_commands_the_row_uses()
    {
        var retry = JsonSerializer.Deserialize("""{"kind":"retry_workflow_step","stepId":"s"}""", EngineJson.Default.EngineCommand);
        var skip = JsonSerializer.Deserialize("""{"kind":"decide_workflow_step","stepId":"s","decision":"skip"}""", EngineJson.Default.EngineCommand);
        Assert.Equal(new RetryWorkflowStep("s"), retry);
        Assert.Equal(new DecideWorkflowStep("s", "skip"), skip);
    }
}
