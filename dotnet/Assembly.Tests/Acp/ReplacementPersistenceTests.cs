using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Acp;

public sealed class ReplacementPersistenceTests
{
    [Fact]
    public void Reopening_the_store_keeps_the_replacement_and_later_append_as_one_item()
    {
        using var file = new TempDatabase();
        using (var db = TestDatabase.Open(file.Path))
        {
            var store = RowStore.Open(db);
            var converter = new RowConverter();
            foreach (var e in new AgentEvent[] {
                new TurnStarted(0, "r", "hi"), new TextChunk(1, "m", "old content"),
                new TextChunk(2, "m", "new", Replace: true), new TextChunk(3, "m", " content"),
                new TurnEnded(4, "end_turn", null) })
                store.Commit("s", converter.Apply(e));
        }
        using var reopened = TestDatabase.Open(file.Path);
        var page = RowStore.Open(reopened).PageBefore("s", long.MaxValue, 20);
        var message = Assert.Single(page.Rows, row => row.ItemId == "m");
        Assert.Equal(RowKind.Message, message.Kind);
        Assert.Contains("new content", message.Payload.GetRawText());
        Assert.DoesNotContain("old", message.Payload.GetRawText());
    }

    [Theory]
    [InlineData(false, false, "new")]
    [InlineData(false, false, "")]
    [InlineData(false, true, "new")]
    [InlineData(false, true, "")]
    [InlineData(true, false, "new")]
    [InlineData(true, false, "")]
    [InlineData(true, true, "new")]
    [InlineData(true, true, "")]
    public void Whole_replacement_clears_every_interrupted_segment_on_reopen(bool thinking, bool tool, string replacement)
    {
        using var file = new TempDatabase();
        using (var db = TestDatabase.Open(file.Path))
        {
            var store = RowStore.Open(db);
            var converter = new RowConverter();
            AgentEvent Chunk(long at, string text, bool replace = false) => thinking
                ? new ThoughtChunk(at, "m", text, replace) : new TextChunk(at, "m", text, replace);
            AgentEvent interruption = tool
                ? new ToolCallChanged(1100, "tool", true, new ToolCallFields(Name: "Read", Status: "completed"))
                : thinking ? new TextChunk(1100, "other", "answer") : new ThoughtChunk(1100, "other", "reason");
            foreach (var e in new AgentEvent[] { new TurnStarted(0, "r", "hi"), Chunk(1000, "old first"),
                interruption, Chunk(1200, "old second"), Chunk(1300, replacement, true) })
                store.Commit("s", converter.Apply(e));
            var current = store.PageBefore("s", long.MaxValue, 50).Rows;
            Assert.All(current, row => Assert.DoesNotContain("old", row.Payload.GetRawText()));
            store.Commit("s", converter.Apply(Chunk(1400, " later")));
            store.Commit("s", converter.Apply(new TurnEnded(1500, "end_turn", null)));
        }
        using var reopened = TestDatabase.Open(file.Path);
        var rows = RowStore.Open(reopened).PageBefore("s", long.MaxValue, 50).Rows;
        Assert.All(rows, row => Assert.DoesNotContain("old", row.Payload.GetRawText()));
        var updated = Assert.Single(rows, row => row.ItemId == (thinking ? "m:thinking:2" : "m:2"));
        Assert.Contains((replacement + " later").Trim(), updated.Payload.GetRawText());
        if (thinking) Assert.True(updated.Payload.GetProperty("done").GetBoolean());
        Assert.Contains(rows, row => row.ItemId == (tool ? "tool" : thinking ? "other" : "other:thinking"));
    }
}
