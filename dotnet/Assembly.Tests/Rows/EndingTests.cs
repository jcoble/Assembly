using Assembly.Engine.Rows;
using static Assembly.Tests.Rows.Converted;

namespace Assembly.Tests.Rows;

/// <summary>How turns end other than cleanly: a failing tool, a stop, an error, and a lost agent.</summary>
public sealed class EndingTests
{
    [Fact]
    public void A_failing_tool_shows_failed_with_its_output_and_the_turn_still_finishes()
    {
        var turn = Fixture("acp/tool-fails.jsonl");
        var bash = turn.OfKind(RowKind.Tool)[^1];
        Assert.Equal("failed", Text(bash, "state"));
        Assert.Equal("ls /definitely-not-here-7731", Text(bash, "summary"));
        Assert.Contains("No such file or directory", turn.Detail(bash.ItemId!).Output);
        Assert.Equal("finished", Text(turn.Rows[^1], "state"));
    }

    [Fact]
    public void A_stopped_turn_keeps_its_text_and_shows_cancelled_not_an_error()
    {
        var turn = Fixture("acp/cancel.jsonl");
        Assert.Equal("1", TextOf(turn.Last(RowKind.Message)));
        Assert.Empty(turn.OfKind(RowKind.Error));
        Assert.Equal("aborted", turn.Last(RowKind.Stats).Payload.GetProperty("status").GetString());
        Assert.Equal("cancelled", Text(turn.Rows[^1], "state"));
    }

    [Fact]
    public void An_error_answer_shows_the_error_and_fails_the_turn()
    {
        var turn = Fixture("acp/error.jsonl");
        Assert.Equal([RowKind.User, RowKind.Turn, RowKind.Error, RowKind.Stats, RowKind.Turn], turn.Rows.Select(row => row.Kind));
        var error = turn.Last(RowKind.Error);
        Assert.Equal("Internal error: Session not found", Text(error, "message"));
        Assert.Equal("-32603", Text(error, "code"));
        Assert.Equal("failed", turn.Last(RowKind.Stats).Payload.GetProperty("status").GetString());
        Assert.Equal("failed", Text(turn.Rows[^1], "state"));
    }

    [Fact]
    public void A_lost_agent_keeps_the_text_so_far_and_fails_the_turn()
    {
        var turn = Fixture("acp/lost.jsonl");
        Assert.Equal("1", TextOf(turn.Last(RowKind.Message)));
        Assert.Equal("agent_lost", Text(turn.Last(RowKind.Error), "code"));
        Assert.Equal([RowKind.Message, RowKind.Error, RowKind.Stats, RowKind.Turn], turn.Rows.TakeLast(4).Select(row => row.Kind));
        Assert.Equal("failed", Text(turn.Rows[^1], "state"));
    }

    private static string TextOf(NewRow message) =>
        Get(message, "blocks")[0].GetProperty("parts")[0].GetProperty("value").GetString()!;
}
