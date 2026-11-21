using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Rows.Copies;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>The owner's reads a saved copy downloads from (TSK-1692): whole items newest first, and each session's facts.</summary>
public sealed class RowsDownloadReadTests : IDisposable
{
    private readonly EngineFixture _f = new();
    private readonly RowStore _rows;

    public RowsDownloadReadTests() => _rows = RowStore.Open(_f.Db);

    [Fact]
    public void Before_returns_whole_items_newest_first_under_the_budget()
    {
        Save("s", 1, "a"); Save("s", 2, "b"); Save("s", 3, "a"); Save("s", 4, null); Save("s", 5, "b"); Save("s", 6, "c");

        var chunk = _rows.History.ItemsBefore("s", null, null);

        Assert.Equal([6L, 4, 2, 5, 1, 3], Seqs(chunk));
        Assert.Equal(new HistoryFacts(1, 6, 6), chunk.Facts);

        // Three items of 400 KB: the third would pass 1 MB, so it waits for the next chunk.
        Save("big", 1, "x", Text(400_000)); Save("big", 2, "y", Text(400_000)); Save("big", 3, "z", Text(400_000));
        Assert.Equal([3L, 2], Seqs(_rows.History.ItemsBefore("big", null, null)));
        Assert.Equal([1L], Seqs(_rows.History.ItemsBefore("big", 2, null)));
        Assert.Empty(_rows.History.ItemsBefore("big", 1, null).Rows);
    }

    // Pins: spec test 5 (store half): an item is never split across chunks.
    [Fact]
    public void Spec05_store_an_item_with_rows_above_the_cut_comes_whole_in_its_own_chunk()
    {
        Save("s", 1, "a"); Save("s", 2, "b"); Save("s", 3, "c"); Save("s", 4, "a"); Save("s", 5, "a");

        Assert.Equal([2L, 1, 4, 5], Seqs(_rows.History.ItemsBefore("s", 3, null)));
    }

    [Fact]
    public void An_item_over_the_budget_comes_alone()
    {
        Save("s", 1, "small");
        Save("s", 2, "huge", Text(800_000)); Save("s", 3, "huge", Text(800_000));

        Assert.Equal([2L, 3], Seqs(_rows.History.ItemsBefore("s", null, null)));
        Assert.Equal([1L], Seqs(_rows.History.ItemsBefore("s", 2, null)));
    }

    [Fact]
    public void First_call_stops_at_100_items()
    {
        for (var seq = 1; seq <= 150; seq++)
            Save("s", seq, $"i{seq}");

        var chunk = _rows.History.ItemsBefore("s", null, RowsFeature.FirstChunkItems);

        Assert.Equal(Enumerable.Range(51, 100).Reverse().Select(seq => (long)seq), Seqs(chunk));
    }

    [Fact]
    public void Facts_count_non_delta_rows_only()
    {
        // A message still streaming keeps its deltas.
        Save("s", 1, "m", kind: "delta"); Save("s", 2, "m", kind: "delta"); Save("s", 3, "t"); Save("s", 4, null, kind: "user");

        Assert.Equal(new HistoryFacts(1, 4, 2), _rows.History.ItemsBefore("s", null, null).Facts);
        Assert.Equal(new HistoryFacts(0, 0, 0), _rows.History.ItemsBefore("empty", null, null).Facts);
    }

    [Fact]
    public async Task Facts_and_rows_come_from_one_snapshot()
    {
        Save("s", 1, "i1");
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var seq = 2; !stop.IsCancellationRequested && seq < 5000; seq++)
                Save("s", seq, $"i{seq}");
        });

        for (var read = 0; read < 200; read++)
        {
            var chunk = _rows.History.ItemsBefore("s", null, 10);
            Assert.Equal(chunk.Facts.Head, Seqs(chunk)[0]);
            Assert.Equal(chunk.Facts.Head, chunk.Facts.Count);
        }
        stop.Cancel();
        await writer;
    }

    [Fact]
    public void Heads_answers_many_sessions()
    {
        Save("s1", 1, "a"); Save("s1", 2, "b");
        Save("s2", 1, "a", kind: "delta"); Save("s2", 2, "b");

        var facts = _rows.History.Facts(["s1", "s2", "unknown"]);

        Assert.Equal(new Dictionary<string, HistoryFacts>
        {
            ["s1"] = new(1, 2, 2),
            ["s2"] = new(1, 2, 1),
        }, facts);
    }

    private void Save(string session, long seq, string? item, string text = "x", string kind = "tool") =>
        Assert.True(_rows.Copy(new StoredRow(session, seq, "run", item, kind,
            JsonSerializer.SerializeToElement(new { text }), seq, PrevSeq: seq - 1)));

    private static string Text(int length) => new('x', length);

    private static long[] Seqs(BeforeChunk chunk) => chunk.Rows.Select(row => row.GetProperty("seq").GetInt64()).ToArray();

    public void Dispose() => _f.Dispose();
}
