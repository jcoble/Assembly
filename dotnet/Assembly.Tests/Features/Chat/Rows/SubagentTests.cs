using Assembly.Domain.Events;
using Assembly.Tests.Fixtures;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>Claude Code's subagent as a child session (clientCapabilities.subagents), as recorded.</summary>
public sealed class SubagentTests
{
    [Fact]
    public void The_child_is_announced_running_then_idle_and_its_prompt_names_the_launching_tool_call()
    {
        var events = FixtureReplay.Events("acp/subagent.jsonl");
        var changes = events.OfType<SubagentChanged>().ToList();
        Assert.Equal(("general-purpose", "Count lines in notes.txt", "running", true),
            (changes[0].Title, changes[0].Description, changes[0].State, changes[0].CanCancel));
        Assert.Equal(("idle", "end_turn", (string?)null), (changes[^1].State, changes[^1].StopReason, changes[^1].Title));
        Assert.Single(changes.Select(change => change.AcpSessionId).Distinct());

        var task = events.OfType<ToolCallChanged>().First(call => call.Started);
        var prompt = Assert.Single(events.OfType<SessionMessage>());
        Assert.Equal(task.ToolCallId, prompt.MessageId);
        Assert.Contains("notes.txt", prompt.Text);
    }
}
