using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Tests.Fixtures;
using Assembly.Infrastructure.Persistence;
using static Assembly.Tests.Features.Chat.Rows.Converted;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>The recorded tool turn: ToolSearch, Read, Bash, and an Edit that asked permission, between two messages.</summary>
public sealed class ToolTurnTests
{
    private const string ToolSearch = "toolu_013PTbSqNKt3gPS7hrwcKe3o";
    private const string Read = "toolu_01SJ4uuZgjqkmu1ovu31fLED";
    private const string Bash = "toolu_01YAmXRA8UPoCAGDrzDwqBJV";
    private const string Edit = "toolu_012mN1u8J2i9vYYpscFbFR29";
    private const string File = "/tmp/assembly-record-tools-9hZYYI/notes.txt";
    private static readonly Converted Turn = Fixture("acp/tools.jsonl");

    [Fact]
    public void Items_appear_in_the_order_the_agent_started_them()
    {
        var items = Turn.ItemOrder().Where(id => !id.StartsWith('#') && !id.StartsWith("context:")).ToList();
        Assert.Equal(
            [ToolSearch, "msg_011CghqGtoTovCoBPYZt8UZM", Read, Bash, Edit, "msg_011CghqHELTYkfVgpsisvF9B"],
            items);
    }

    [Fact]
    public void A_message_ends_when_the_next_tool_starts()
    {
        var message = Turn.OfItem("msg_011CghqGtoTovCoBPYZt8UZM")[^1];
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
        AssertLine(ToolSearch, "ToolSearch", "todo task list");
        AssertLine(Read, "Read", File);
        AssertLine(Bash, "Bash", "wc -l notes.txt");
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
        using var file = new TempDatabase();
        using var database = TestDatabase.Open(file.Path);
        var store = RowStore.Open(database);
        var converter = new RowConverter();
        foreach (var agentEvent in FixtureReplay.Events("acp/tools.jsonl"))
            store.Commit("session", converter.Apply(agentEvent));
        Assert.Equal("3 notes.txt", store.ToolCall("session", Bash)!.Output);
        Assert.Contains("\"command\":\"wc -l", Turn.Detail(Bash).Input);
    }

    [Fact]
    public void The_stats_count_the_tool_calls()
    {
        var stats = Turn.Last(RowKind.Stats).Payload;
        Assert.Equal(4, stats.GetProperty("toolCalls").GetInt32());
        Assert.Equal(0.2324636m, stats.GetProperty("costUsd").GetDecimal());
        Assert.Equal("finished", Text(Turn.Rows[^1], "state"));
    }

    private static void AssertLine(string tool, string name, string summary)
    {
        var line = Turn.OfItem(tool)[^1];
        Assert.Equal(name, Text(line, "name"));
        Assert.Equal(summary, Text(line, "summary"));
    }
}
