using Assembly.Engine.Workflows.Reviews;

namespace Assembly.Tests.Engine.Workflows.Reviews;

public sealed class ReviewRoundsTests
{
    [Fact]
    public void Mixed_previous_and_new_findings_pause_as_old_workflow_requires()
    {
        // mac-command-bar d5f5fe68, workflow/mod.rs2119–2125: any trimmed repeated finding stops.
        Assert.True(ReviewRounds.ShouldPause([" earlier defect "], ["earlier defect", "new defect"], 1, 5));
    }

    [Fact]
    public void New_findings_continue_and_ordinal_case_remains_significant()
    {
        Assert.False(ReviewRounds.ShouldPause(["Defect"], ["defect", "new defect"], 1, 5));
    }

    [Fact]
    public void Explicit_repeat_marker_and_recorded_budget_pause()
    {
        Assert.True(ReviewRounds.ShouldPause([], [" REPEAT: still broken "], 0, 5));
        Assert.False(ReviewRounds.ShouldPause([], ["new"], 4, 5));
        Assert.True(ReviewRounds.ShouldPause([], ["new"], 5, 5));
    }
}
