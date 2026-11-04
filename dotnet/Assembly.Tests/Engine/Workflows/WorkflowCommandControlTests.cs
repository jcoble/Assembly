using Assembly.Engine.Workflows.Runs;
using Assembly.Tests.Engine.Workflows.Execution;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowCommandControlTests
{
    [Fact]
    public async Task Pause_allows_the_current_multicommand_step_to_finish()
    {
        using var f = new CommandStepTests.Fixture("printf first", "printf second");
        Assert.True(f.Start(f.Prepare()).Applied);
        var run = f.Core.Text("SELECT id FROM engine_run")!;
        Assert.True(f.Core.Apply(new PauseWorkflow(), run, f.Core.Number("SELECT version FROM engine_run")).Applied);
        var first = f.Claim(); Assert.True(f.Complete(first, await f.Host().ExecuteAsync(first, default)).Applied);
        var continuation = f.Start(f.Prepare()); Assert.True(continuation.Applied, continuation.Error);
        var second = f.Claim(); Assert.True(f.Complete(second, await f.Host().ExecuteAsync(second, default)).Applied);
        Assert.Equal("succeeded", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal(1, f.Core.Number("SELECT paused FROM engine_run"));
        Assert.Equal(1, f.Core.Count("engine_session"));
    }
}
