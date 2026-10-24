using Assembly.Engine.Events;
using Assembly.Engine.Rows;
using static Assembly.Tests.Rows.Converted;

namespace Assembly.Tests.Rows;

/// <summary>Coalescing and the delta's <c>from</c>, with made-up events.</summary>
public sealed class StreamingTests
{
    private static readonly TurnStarted Start = new(0, "run-1", "go");

    [Fact]
    public void The_first_chunk_goes_out_at_once_and_a_quick_second_waits()
    {
        var rows = From([Start, new TextChunk(1000, "m", "Hello"), new TextChunk(1050, "m", " there")]).OfKind(RowKind.Delta);
        Assert.Equal("Hello", Text(Assert.Single(rows), "text"));
    }

    [Fact]
    public void A_tick_lets_held_text_out_once_its_window_has_passed()
    {
        var converter = new RowConverter();
        converter.Apply(Start);
        converter.Apply(new TextChunk(1000, "m", "Hello"));
        converter.Apply(new TextChunk(1050, "m", " there"));
        Assert.Empty(converter.Apply(new ClockTick(1080)).Rows);
        var delta = Assert.Single(converter.Apply(new ClockTick(1100)).Rows);
        Assert.Equal(" there", Text(delta, "text"));
        Assert.Empty(converter.Apply(new ClockTick(1300)).Rows);
    }

    [Fact]
    public void From_points_at_the_first_block_that_changed()
    {
        var rows = From([Start, new TextChunk(1000, "m", "One.\n\nTwo"), new TextChunk(1200, "m", " more.")]).OfKind(RowKind.Delta);
        Assert.Equal(0, Get(rows[0], "from").GetInt32());
        Assert.Equal(1, Get(rows[1], "from").GetInt32());
        Assert.Equal(1, Get(rows[1], "blocks").GetArrayLength());
    }

    [Fact]
    public void Text_that_changes_no_block_waits_and_is_not_lost()
    {
        var rows = From([Start, new TextChunk(1000, "m", "Hi"), new TextChunk(1200, "m", "\n\n"), new TextChunk(1400, "m", "Bye")]).OfKind(RowKind.Delta);
        Assert.Equal(["Hi", "\n\nBye"], rows.Select(row => Text(row, "text")));
    }

    [Fact]
    public void A_new_agent_message_id_ends_the_previous_message()
    {
        var turn = From([Start, new TextChunk(1000, "a", "First"), new TextChunk(1010, "b", "Second"), new TurnEnded(1020, "end_turn", null)]);
        Assert.Equal(["a", "b"], turn.OfKind(RowKind.Message).Select(row => row.ItemId));
    }

    [Fact]
    public void An_update_after_a_tool_finished_adds_no_row()
    {
        var done = new ToolCallFields(Name: "Bash", Status: "completed", Output: "ok");
        var late = new ToolCallFields(ContentText: "late");
        var turn = From([Start, new ToolCallChanged(1000, "t", true, new ToolCallFields(Name: "Bash")),
            new ToolCallChanged(1100, "t", false, done), new ToolCallChanged(1200, "t", false, late)]);
        Assert.Equal(["started", "completed"], turn.OfItem("t").Select(row => Text(row, "state")));
        Assert.Equal(1, Converted.From([Start, new ToolCallChanged(1000, "t", true, new ToolCallFields(Name: "Bash")),
            new ToolCallChanged(1100, "t", false, done), new ToolCallChanged(1200, "t", false, late), new TurnEnded(1300, "end_turn", null)])
            .Last(RowKind.Stats).Payload.GetProperty("toolCalls").GetInt32());
    }

    [Fact]
    public void An_update_after_the_turn_ended_leaves_a_tool_the_end_failed_as_it_was()
    {
        // As on Workbox Test: a Codex command stopped as its turn ended, then a bare update for it once the turn was over.
        var turn = From([Start, new ToolCallChanged(1000, "exec-1", true, new ToolCallFields(Name: "sleep 60", Kind: "execute")),
            new TurnEnded(1100, "end_turn", null), new ToolCallChanged(1200, "exec-1", false, new ToolCallFields())]);
        Assert.Equal(["started", "failed"], turn.OfItem("exec-1").Select(row => Text(row, "state")));
        Assert.All(turn.OfItem("exec-1"), row => Assert.Equal("sleep 60", Text(row, "name")));
    }

    [Fact]
    public void Thinking_that_resumes_after_a_tool_is_a_new_item()
    {
        var turn = From([Start, new ThoughtChunk(1000, "m", "First."), new ToolCallChanged(1100, "t", true, new ToolCallFields(Name: "Read")),
            new ThoughtChunk(1200, "m", "Second.")]);
        Assert.Equal(2, turn.OfKind(RowKind.Thinking).Select(row => row.ItemId).Distinct().Count());
    }

    [Fact]
    public void Payload_text_is_written_as_is_not_escaped()
    {
        var delta = From([Start, new TextChunk(1000, "m", "It's `done` ☑")]).OfKind(RowKind.Delta)[0];
        Assert.Contains("It's `done` ☑", delta.Payload.GetRawText());
    }
}
