using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Rows.Copies;
using Assembly.Domain.Rows;
using Assembly.Tests.Draft.Base;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>
/// Pages of a saved copy still downloading (TSK-1692): a page at the download's frontier says there is more, and one
/// below it waits for the next chunk, the end of the download or 10 s, whichever comes first.
/// </summary>
public sealed class DownloadPagingTests : IDisposable
{
    private const string Box = "box-engine";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly EngineFixture _f = new();
    private readonly ClockSocketTests.CountingTime _time = new(DateTimeOffset.UnixEpoch);
    private readonly LiveHub _hub;

    public DownloadPagingTests()
    {
        _hub = new LiveHub(RowStore.Open(_f.Db), time: _time);
        // The newest 100 of 300 rows are saved; the rest are still on the remote.
        _hub.StartCopy("s", Box, Chunk(new HistoryFacts(1, 300, 300), Rows(201, 300)));
    }

    // Pins: spec test 4.
    [Fact]
    public async Task Spec04_a_frontier_page_says_more_and_the_next_page_arrives_with_its_chunk()
    {
        _hub.BeginDownload("s");
        var top = await _hub.PageAsync("s", long.MaxValue, 100, default);
        Assert.Equal(Range(201, 300), top.Rows.Select(row => row.Seq));
        Assert.True(top.HasBefore);

        var next = _hub.PageAsync("s", 201, 100, default);
        Assert.False(next.IsCompleted);
        _hub.Backfill("s", Rows(151, 200));

        var page = await next.WaitAsync(Wait);
        Assert.Equal(Range(151, 200), page.Rows.Select(row => row.Seq));
        Assert.True(page.HasBefore);
    }

    // Pins: review focus 3.
    [Fact]
    public async Task A_frontier_page_with_no_running_download_answers_at_once()
    {
        var page = _hub.PageAsync("s", 201, 100, default);

        Assert.True(page.IsCompleted);
        Assert.Empty((await page).Rows);
        Assert.True((await page).HasBefore);
        Assert.Equal(0, _time.Timers);
    }

    [Fact]
    public async Task A_frontier_page_on_a_hung_download_returns_after_10s_and_its_timer_is_gone()
    {
        _hub.BeginDownload("s");
        var page = _hub.PageAsync("s", 201, 100, default);
        Assert.Equal(1, _time.Timers);

        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(page.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));

        var answer = await page.WaitAsync(Wait);
        Assert.Empty(answer.Rows);
        Assert.True(answer.HasBefore);
        Assert.Equal(0, _time.Timers);
    }

    [Fact]
    public async Task A_download_that_completes_during_the_wait_says_no_more_history()
    {
        _hub.BeginDownload("s");
        var page = _hub.PageAsync("s", 201, 100, default);

        _hub.CompleteCopy("s");
        _hub.EndDownload("s");

        var answer = await page.WaitAsync(Wait);
        Assert.Empty(answer.Rows);
        Assert.False(answer.HasBefore);
        Assert.Equal(0, _time.Timers);
    }

    [Fact]
    public async Task A_cancelled_frontier_page_releases_its_wait()
    {
        _hub.BeginDownload("s");
        using var leaving = new CancellationTokenSource();
        var page = _hub.PageAsync("s", 201, 100, leaving.Token);
        Assert.False(page.IsCompleted);

        leaving.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page.WaitAsync(Wait));
        Assert.Equal(0, _time.Timers);
    }

    [Fact]
    public async Task A_complete_copy_pages_as_a_local_session_does()
    {
        _hub.Backfill("s", Rows(1, 200));
        _hub.CompleteCopy("s");

        var page = await _hub.PageAsync("s", 101, 100, default);

        Assert.Equal(Range(1, 100), page.Rows.Select(row => row.Seq));
        Assert.False(page.HasBefore);
    }

    private static IEnumerable<long> Range(long from, long to) => Enumerable.Range((int)from, (int)(to - from + 1)).Select(seq => (long)seq);

    private static StoredRow[] Rows(long from, long to) => [.. Range(from, to).Select(seq =>
        new StoredRow("s", seq, "run", $"item-{seq}", RowKind.Tool, JsonSerializer.SerializeToElement(new { text = $"row {seq}" }), seq))];

    private static BeforeChunk Chunk(HistoryFacts facts, StoredRow[] rows) =>
        new([.. rows.OrderByDescending(row => row.Seq).Select(RowWire.ToElement)], facts);

    public void Dispose()
    {
        _hub.Dispose();
        _f.Dispose();
    }
}
