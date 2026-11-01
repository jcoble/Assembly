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
}
