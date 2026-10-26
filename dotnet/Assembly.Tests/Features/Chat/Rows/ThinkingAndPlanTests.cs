using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using static Assembly.Tests.Features.Chat.Rows.Converted;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>Thinking and plans, from the hand-written fixtures (see Fixtures/handmade/README.md).</summary>
public sealed class ThinkingAndPlanTests
{
    [Fact]
    public void Thinking_streams_whole_coalesced_then_ends_done_when_the_answer_starts()
    {
        var turn = Fixture("handmade/thinking.jsonl");
        var thinking = turn.OfKind(RowKind.Thinking);
        Assert.Equal(
            [("Let me ", false), ("Let me think about this.", false), ("Let me think about this.", true)],
            thinking.Select(row => (Text(row, "text"), Get(row, "done").GetBoolean())));
        Assert.Single(thinking.Select(row => row.ItemId).Distinct());
        Assert.True(turn.Rows.ToList().IndexOf(thinking[^1]) < turn.Rows.ToList().IndexOf(turn.OfKind(RowKind.Delta)[0]));
    }

    [Fact]
    public void A_message_with_no_agent_id_still_gets_one_item()
    {
        var turn = Fixture("handmade/thinking.jsonl");
        var ids = turn.Rows.Where(row => row.Kind is RowKind.Delta or RowKind.Message).Select(row => row.ItemId).Distinct().ToList();
        Assert.NotNull(Assert.Single(ids));
        Assert.DoesNotContain(ids[0], turn.OfKind(RowKind.Thinking).Select(row => row.ItemId));
    }

    [Fact]
    public void Each_plan_update_replaces_the_plan()
    {
        var plans = Fixture("handmade/plan.jsonl").OfKind(RowKind.Plan);
        Assert.Equal(2, plans.Count);
        Assert.Single(plans.Select(row => row.ItemId).Distinct());
        var items = Get(plans[^1], "items").EnumerateArray().Select(item => (item.GetProperty("text").GetString(), item.GetProperty("status").GetString()));
        Assert.Equal([("Read the file", "completed"), ("Fix the bug", "in_progress")], items);
    }
}
