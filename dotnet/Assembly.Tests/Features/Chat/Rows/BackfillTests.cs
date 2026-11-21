using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Rows.Copies;
using Assembly.Domain.Rows;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>A download's saves through the hub (TSK-1692): the first chunk, older chunks, and a live row racing them.</summary>
public sealed class BackfillTests : IDisposable
{
    private const string Box = "box-engine";
    private readonly EngineFixture _f = new();
    private readonly RowStore _store;
    private readonly LiveHub _hub;
    private readonly List<string> _summaryChanged = [];

    public BackfillTests()
    {
        _store = RowStore.Open(_f.Db);
        _hub = new LiveHub(_store, summaries: SessionSummaries.Load(_store), summaryChanged: _summaryChanged.Add);
    }

    [Fact]
    public void Backfill_skips_rows_already_saved()
    {
        _hub.StartCopy("s", Box, Chunk(new HistoryFacts(1, 6, 6), Row(5), Row(6)));

        var saved = _hub.Backfill("s", [Row(3), Row(4), Row(5)]);

        Assert.Empty(saved);
        Assert.Equal([3L, 4, 5, 6], Seqs("s"));
    }

    [Fact]
    public void Backfill_deletes_deltas_of_finished_messages()
    {
        _hub.StartCopy("s", Box, Chunk(new HistoryFacts(1, 9, 3), Row(8, "m2", RowKind.Message), Row(9)));

        _hub.Backfill("s", [Row(1, "m1", RowKind.Delta), Row(2, "m1", RowKind.Delta), Row(3, "m1", RowKind.Message),
            Row(6, "m2", RowKind.Delta)]);

        Assert.Equal([3L, 8, 9], Seqs("s"));
    }

    [Fact]
    public void Backfill_pushes_nothing_and_leaves_the_summary()
    {
        _hub.StartCopy("s", Box, Chunk(new HistoryFacts(1, 9, 9), Row(9)));
        _summaryChanged.Clear();
        using var viewer = _hub.Subscribe("s", _hub.LastSeq("s"));

        _hub.Backfill("s", [Row(1, null, RowKind.Turn, new { state = "started" }), Row(2)]);

        Assert.False(viewer.Queue.Reader.TryRead(out _));
        Assert.Empty(_summaryChanged);
    }

    // Review focus 1: a live row the follower copied before the first chunk is saved once, and the next one follows on.
    [Fact]
    public void Chunk_racing_a_live_copy_saves_each_row_once()
    {
        for (var seq = 1; seq <= 3; seq++) Assert.True(_hub.PublishCopy(Row(seq) with { PrevSeq = seq - 1 }));

        _hub.StartCopy("s", Box, Chunk(new HistoryFacts(1, 5, 5), Row(2), Row(3), Row(4), Row(5)));

        Assert.Equal([1L, 2, 3, 4, 5], Seqs("s"));
        Assert.True(_hub.PublishCopy(Row(6) with { PrevSeq = 5 }));
    }

    [Fact]
    public void StartCopy_never_lowers_next_seq()
    {
        for (var seq = 1; seq <= 7; seq++) Assert.True(_hub.PublishCopy(Row(seq) with { PrevSeq = seq - 1 }));

        _hub.StartCopy("s", Box, Chunk(new HistoryFacts(1, 5, 5), Row(5)));

        Assert.Equal(7, _hub.LastSeq("s"));
    }

    [Fact]
    public void StartCopy_of_an_empty_session_is_complete()
    {
        var start = _hub.StartCopy("s", Box, Chunk(new HistoryFacts(0, 0, 0)));

        Assert.True(start.Complete);
        Assert.Equal(new RemoteCopy("s", Box, true, 0), _hub.Copies.RemoteCopyOf("s"));
        Assert.Equal(0, _hub.LastSeq("s"));
    }

    [Fact]
    public void StartCopy_returns_its_subagent_rows()
    {
        var card = Row(4, "subagent:native", RowKind.Subagent, new { sessionId = "child", state = "running" });

        var start = _hub.StartCopy("s", Box, Chunk(new HistoryFacts(1, 5, 5), Row(3), card, Row(5)));

        Assert.False(start.Complete);
        Assert.Equal([4L], start.SubagentRows.Select(row => row.Seq));
        Assert.Equal(new RemoteCopy("s", Box, false, 5), _hub.Copies.RemoteCopyOf("s"));
        Assert.Equal(5, _hub.LastSeq("s"));
    }

    private static StoredRow Row(long seq, string? item = null, string kind = RowKind.Tool, object? payload = null) =>
        new("s", seq, "run", item ?? $"item-{seq}", kind, JsonSerializer.SerializeToElement(payload ?? new { text = $"row {seq}" }), seq);

    private static BeforeChunk Chunk(HistoryFacts facts, params StoredRow[] rows) =>
        new([.. rows.OrderByDescending(row => row.Seq).Select(RowWire.ToElement)], facts);

    private long[] Seqs(string session) =>
        [.. _f.Db.Query("SELECT seq FROM rows WHERE session_id=$s ORDER BY seq", row => row.Long(0), default, new SqlArg("$s", session))];

    public void Dispose()
    {
        _hub.Dispose();
        _f.Dispose();
    }
}
