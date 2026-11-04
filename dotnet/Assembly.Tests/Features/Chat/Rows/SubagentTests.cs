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
        Assert.Equal(("Count lines in notes.txt", "Explore", "running", true),
            (changes[0].Title, changes[0].Description, changes[0].State, changes[0].CanCancel));
        Assert.Equal(("idle", "end_turn", "Count lines in notes.txt"), (changes[^1].State, changes[^1].StopReason, changes[^1].Title));
        Assert.Single(changes.Select(change => change.AcpSessionId).Distinct());

        // Native v2 messages keep provider provenance separate from user prompt acknowledgements.
        var messages = events.OfType<ProviderSessionMessage>().ToList();
        Assert.Equal(3, messages.Count);
        var prompt = messages[0];
        Assert.Equal("toolu_01Wbg3nPKQrpMpy7cWVoZBm6", prompt.MessageId);
        Assert.Equal(changes[0].AcpSessionId, prompt.RecipientSessionId);
        Assert.Equal(prompt.MessageId, prompt.RecipientMessageId);
        Assert.Equal(prompt.MessageId, messages[1].SenderMessageId);
        Assert.Equal(prompt.RecipientSessionId, messages[1].SessionId);
        Assert.Equal(changes[0].AcpSessionId, messages[2].SenderSessionId);
        Assert.Contains("notes.txt", prompt.Content[0].GetProperty("text").GetString());
        Assert.Empty(events.OfType<SessionMessage>());
    }
}
