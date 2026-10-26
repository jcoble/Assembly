using Assembly.Domain.Events;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Tests.Fixtures;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>Both agents list their slash commands in <c>available_commands_update</c>; a command is sent as a prompt.</summary>
public sealed class CommandsTests
{
    [Theory]
    [InlineData("acp/slash-context.jsonl", "context")]
    [InlineData("acp/codex-slash-status.jsonl", "status")]
    public void The_agents_command_list_is_read_and_adds_no_rows(string fixture, string command)
    {
        var events = FixtureReplay.Events(fixture);
        var names = events.OfType<CommandsChanged>().Last().Commands.EnumerateArray().Select(c => c.GetProperty("name").GetString());
        Assert.Contains(command, names);

        // The command's answer is an ordinary reply. (Claude Code sends its /context answer twice, under two message ids.)
        var turn = Converted.From(events);
        Assert.NotEmpty(turn.OfKind(RowKind.Message));
        Assert.Empty(turn.OfKind(RowKind.Tool));
    }

    [Fact]
    public void A_commands_input_hint_comes_through()
    {
        var review = FixtureReplay.Events("acp/codex-slash-status.jsonl").OfType<CommandsChanged>().Last()
            .Commands.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "review-branch");
        Assert.Equal("branch name", review.GetProperty("input").GetProperty("hint").GetString());
    }
}
