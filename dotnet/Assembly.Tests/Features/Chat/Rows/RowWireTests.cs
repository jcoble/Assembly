using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Application.Features.Chat;

namespace Assembly.Tests.Features.Chat.Rows;

public sealed class RowWireTests
{
    [Fact]
    public void A_row_read_back_from_its_wire_json_is_the_same_row()
    {
        // The Mac reads the box's rows off the wire and saves them as they are.
        var payload = JsonDocument.Parse("""{"name":"Bash","state":"completed","summary":"ls \"x\"","hasDetail":true}""").RootElement;
        var row = new StoredRow("thread-1", 7, "run-1", "t1", RowKind.Tool, payload, 1_792_000_000_000, PrevSeq: 4);
        var noIds = new StoredRow("thread-1", 8, null, null, RowKind.Turn, JsonDocument.Parse("""{"state":"started"}""").RootElement, 5);

        foreach (var original in new[] { row, noIds })
        {
            var read = RowWire.Read(JsonDocument.Parse(RowWire.ToJson(original)).RootElement);
            Assert.Equal(RowWire.ToJson(original), RowWire.ToJson(read));
            Assert.Equal((original.ThreadId, original.Seq, original.RunId, original.ItemId, original.Kind, original.CreatedAt, original.PrevSeq),
                (read.ThreadId, read.Seq, read.RunId, read.ItemId, read.Kind, read.CreatedAt, read.PrevSeq));
        }
    }
}
