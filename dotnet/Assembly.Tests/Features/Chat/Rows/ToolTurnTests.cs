using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using static Assembly.Tests.Features.Chat.Rows.Converted;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>The recorded tool turn: ToolSearch, Read, Bash, and an Edit that asked permission, between three messages.</summary>
public sealed class ToolTurnTests
{
    private const string ToolSearch = "toolu_01WJArcq72dgAfMLbKWhuQqN";
    private const string Read = "toolu_01VxBwJRtaXUo3M5ZGuxGoVz";
    private const string Bash = "toolu_01QAXNGNLNoxzu1d8X4iRQFD";
    private const string Edit = "toolu_01AQLCH8NyrUYyEzzHhVS93F";
    private const string File = "/tmp/spike-record-tools-LWZCTs/notes.txt";
    private static readonly Converted Turn = Fixture("acp/tools.jsonl");

    [Fact]
    public void Items_appear_in_the_order_the_agent_started_them()
    {
        var items = Turn.ItemOrder().Where(id => !id.StartsWith('#') && !id.StartsWith("context:")).ToList();
        Assert.Equal(
            [ToolSearch, "msg_011CgLZKJP9x6gwJFiUpHax8", Read, Bash, "msg_011CgLZKZ3DeAMqPxmg8gcQE", Edit, "msg_011CgLZKjXkemKZZSDjE4LVt"],
            items);
    }

    [Fact]
    public void A_message_ends_when_the_next_tool_starts()
    {
        var message = Turn.OfItem("msg_011CgLZKJP9x6gwJFiUpHax8")[^1];
        Assert.Equal(RowKind.Message, message.Kind);
        Assert.True(Turn.Rows.ToList().IndexOf(message) < Turn.Rows.ToList().IndexOf(Turn.OfItem(Read)[0]));
    }

    [Fact]
    public void Every_tool_starts_as_started_and_ends_completed()
    {
        foreach (var tool in new[] { ToolSearch, Read, Bash, Edit })
        {
            var rows = Turn.OfItem(tool);
            Assert.All(rows, row => Assert.Equal(RowKind.Tool, row.Kind));
            Assert.Equal("started", Text(rows[0], "state"));
            Assert.Equal("completed", Text(rows[^1], "state"));
        }
    }

    [Fact]
    public void A_tool_row_is_written_only_when_its_line_changes()
    {
        foreach (var tool in new[] { ToolSearch, Read, Bash, Edit })
        {
            var payloads = Turn.OfItem(tool).Select(row => row.Payload.GetRawText()).ToList();
            Assert.Equal(payloads.Count, payloads.Distinct().Count());
        }
    }

    [Fact]
    public void Each_tool_line_names_the_tool_and_says_what_it_acted_on()
    {
        AssertLine(ToolSearch, "ToolSearch", "todo");
        AssertLine(Read, "Read", File);
        AssertLine(Bash, "Bash", $"wc -l {File}");
        AssertLine(Edit, "Edit", File);
        Assert.Equal(File, Text(Turn.OfItem(Read)[^1], "path"));
    }

    [Fact]
    public void The_edit_counts_its_lines_and_keeps_its_diff()
    {
        var edit = Turn.OfItem(Edit)[^1];
        Assert.Equal(1, Get(edit, "added").GetInt32());
        Assert.Equal(1, Get(edit, "removed").GetInt32());
        Assert.True(Get(edit, "hasDetail").GetBoolean());

        var diff = Turn.Detail(Edit).Diff!;
        Assert.Contains("\n-apple\n+pear\n", diff);
        Assert.Equal(File, Turn.Detail(Edit).Path);
    }

    [Fact]
    public void Tool_details_hold_the_input_and_the_output()
    {
        Assert.Equal("1\tapple\n2\tbanana\n3\tcherry\n4\t", Turn.Detail(Read).Output);
        Assert.Equal($"3 {File}", Turn.Detail(Bash).Output);
        Assert.Contains("\"command\":\"wc -l", Turn.Detail(Bash).Input);
    }

    [Fact]
    public void The_stats_count_the_tool_calls()
    {
        var stats = Turn.Last(RowKind.Stats).Payload;
        Assert.Equal(4, stats.GetProperty("toolCalls").GetInt32());
        Assert.Equal(0.1562456m, stats.GetProperty("costUsd").GetDecimal());
        Assert.Equal("finished", Text(Turn.Rows[^1], "state"));
    }

    private static void AssertLine(string tool, string name, string summary)
    {
        var line = Turn.OfItem(tool)[^1];
        Assert.Equal(name, Text(line, "name"));
        Assert.Equal(summary, Text(line, "summary"));
    }
}
