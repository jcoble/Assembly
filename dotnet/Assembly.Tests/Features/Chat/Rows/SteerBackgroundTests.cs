using System.Text;
using Assembly.Domain.Events;
using Assembly.Tests.Fixtures;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>The recorded v2 steer returns a terminal background notice and terminal end, rather than v1 task events.</summary>
public sealed class SteerBackgroundTests
{
    [Fact]
    public void A_steered_command_reports_its_background_notice_and_end_through_the_v2_terminal()
    {
        var events = FixtureReplay.Events("acp/steer-background.jsonl");
        var terminals = events.OfType<TerminalChanged>().ToList();
        var terminal = Assert.Single(terminals.Select(change => change.TerminalId).Distinct());
        Assert.Equal("toolu_01W9UQgFiPQYz141ERjCbs5N", terminal);
        var output = Assert.Single(terminals, change => change.Data is not null);
        Assert.True(output.OutputReplaced);
        Assert.Contains("Command was moved to the background", Encoding.UTF8.GetString(Convert.FromBase64String(output.Data!)));
        Assert.True(terminals[^1].ExitStatusReplaced);
        Assert.NotNull(terminals[^1].ExitStatus);
        Assert.Equal("end_turn", Assert.Single(events.OfType<TurnEnded>()).StopReason);
        Assert.Empty(events.OfType<TaskChanged>());
        Assert.Empty(events.OfType<AgentLost>());
    }
}
