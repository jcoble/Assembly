using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows.Reviews;
using Assembly.Engine.Workflows.Scheduling;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Workflows.Execution;

public sealed partial class WorkflowAdvancementTests
{
    [Fact]
    public void Owner_additional_round_consumes_exact_repeat_stop_once_and_preserves_both_journals()
    {
        using var f = new Fixture(new Topics());
        Settle(f, "REPEAT: first finding", "second finding");
        var stoppedContext = AdvanceContext(f);
        Assert.Equal(1, f.Host.AdvancePending(default));
        Assert.Equal(1, f.Core.Number("SELECT paused FROM engine_run"));
        Assert.Equal(0, f.Core.Number("SELECT generation FROM engine_step WHERE id='build'"));
        var stopped = f.Core.Text("SELECT safe_body_json FROM engine_workflow_history WHERE kind='redo_stopped'");
        var definition = f.Core.Text("SELECT snapshot_json FROM engine_workflow_snapshot");
        var owner = OwnerRoundContext(f, new(ActorKind.Session, "owner"));
        var forbidden = f.Core.Db.Writer.Write("test.other-root-cannot-grant", s => ReviewTransitions.AllowAdditionalRound(s,
            owner with { CommandId = Guid.NewGuid(), Actor = new(ActorKind.Session, "another-root") }));
        Assert.Equal("forbidden", forbidden.Error);
        var granted = Grant(f, owner); Assert.True(granted.Applied, granted.Error);
        Assert.Equal(0, f.Core.Number("SELECT paused FROM engine_run"));
        Assert.Equal(owner.ExpectedVersion + 1, f.Core.Number("SELECT version FROM engine_step WHERE id='build'"));
        Assert.Equal(5, f.Core.Number("SELECT max_rounds FROM engine_run"));
        Assert.Equal(definition, f.Core.Text("SELECT snapshot_json FROM engine_workflow_snapshot"));
        Assert.Equal(1, f.Host.AdvancePending(default));
        Assert.Equal(1, f.Core.Number("SELECT generation FROM engine_step WHERE id='build'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_round_consumed'"));
        Assert.Equal(stopped, f.Core.Text("SELECT safe_body_json FROM engine_workflow_history WHERE kind='redo_stopped'"));
        var events = f.Core.Count("engine_event"); var journals = f.Core.Count("engine_command_receipt");
        Assert.Equal(granted, Grant(f, owner with { NowMs = 9000 }));
        var oldStop = f.Core.Db.Writer.Write("test.original-stop-replay", s => f.Commands.AdvanceWorkflowGeneration(s, stoppedContext with { NowMs = 9000 }));
        Assert.True(oldStop.Applied, oldStop.Error);
        Assert.Equal(events, f.Core.Count("engine_event")); Assert.Equal(journals, f.Core.Count("engine_command_receipt"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_round_granted'"));
        f.NextRound(); Settle(f, "REPEAT: first finding", "different second finding");
        Assert.Equal(1, f.Host.AdvancePending(default));
        Assert.Equal(1, f.Core.Number("SELECT generation FROM engine_step WHERE id='build'"));
        Assert.Equal(1, f.Core.Number("SELECT paused FROM engine_run"));
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_stopped'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_round_consumed'"));
    }

    [Fact]
    public void Recorded_target_allowance_extends_budget_without_changing_definition()
    {
        using var f = new Fixture(new Topics());
        Settle(f, "REPEAT: owner permits one more", "second finding");
        Assert.Equal(1, f.Host.AdvancePending(default));
        var grant = OwnerRoundContext(f, new(ActorKind.Window, "window"));
        Assert.True(Grant(f, grant).Applied);
        Assert.Equal(1, f.Host.AdvancePending(default));
        f.NextRound();
        // Boundary fixture: five completed rounds with one already-consumed explicit allowance.
        f.Core.Db.Writer.Write("test.budget-boundary", s => s.Execute("UPDATE engine_step SET round=5 WHERE id='build'"));
        Settle(f, "new finding at base budget", "another new finding");
        Assert.Equal(1, f.Host.AdvancePending(default));
        Assert.Equal(2, f.Core.Number("SELECT generation FROM engine_step WHERE id='build'"));
        Assert.Equal(6, f.Core.Number("SELECT round FROM engine_step WHERE id='build'"));
        Assert.Equal(0, f.Core.Number("SELECT paused FROM engine_run"));
        Assert.Equal(5, f.Core.Number("SELECT max_rounds FROM engine_run"));
        f.NextRound(); Settle(f, "new finding after allowance", "new second after allowance");
        Assert.Equal(1, f.Host.AdvancePending(default));
        Assert.Equal(2, f.Core.Number("SELECT generation FROM engine_step WHERE id='build'"));
        Assert.Equal(1, f.Core.Number("SELECT paused FROM engine_run"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_round_granted'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_round_consumed'"));
    }

    private static void Settle(Fixture f, string first, string second)
    {
        f.Finding("review1", first); f.Finding("review2", second);
        foreach (var step in new[] { "build", "review1", "review2" }) { f.Terminal(step); f.Release(step); }
    }
    private static CommandContext AdvanceContext(Fixture f)
    {
        var generation = f.Core.Number("SELECT generation FROM engine_step WHERE id='build'");
        var version = f.Core.Number("SELECT version FROM engine_step WHERE id='build'");
        return new(DispatchIdentity.Id("workflow-generation-advance/v1", f.Core.Machine, "r", "build",
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture), version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            f.Core.Machine, new(ActorKind.System, "engine.worker"), "build", version, f.Core.Number("SELECT authority_epoch FROM engine_step WHERE id='build'"), 1000);
    }
    private static CommandContext OwnerRoundContext(Fixture f, EngineActor actor) => new(Guid.NewGuid(), f.Core.Machine, actor, "build",
        f.Core.Number("SELECT version FROM engine_step WHERE id='build'"), f.Core.Number("SELECT epoch FROM engine_session WHERE id='owner'"), 1000);
    private static CommandResult Grant(Fixture f, CommandContext context) => f.Core.Db.Writer.Write("test.prepared-owner-additional-round", s =>
        EngineJournal.Execute(s, context, "\"owner-additional-round\"", () => ReviewTransitions.AllowAdditionalRound(s, context)));
}
