using Assembly.Domain.Events;
using Assembly.Tests.Fixtures;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>Claude Code moving a foreground command to the background when a message is steered in, as recorded.</summary>
public sealed class SteerBackgroundTests
{
    [Fact]
    public void A_command_moved_to_the_background_by_a_steer_is_an_untracked_task_and_its_cycle_ends_it()
    {
        var events = FixtureReplay.Events("acp/steer-background.jsonl");
        var task = Assert.Single(events.OfType<TaskChanged>());
        Assert.Equal(("bkwa3yo8n", "running", false), (task.TaskId, task.State, task.Tracked));
        Assert.Contains(events.OfType<CycleEnded>(), cycle => cycle.EndsUntracked);
    }
}
