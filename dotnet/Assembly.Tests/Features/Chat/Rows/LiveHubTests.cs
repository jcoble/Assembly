using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Persistence;
using Assembly.Domain.Rows;
using Assembly.Application.Features.Chat;

namespace Assembly.Tests.Features.Chat.Rows;

public sealed class LiveHubTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly TempDatabase _db = new();
    private readonly AppDatabase _database;
    private readonly RowStore _store;

    public LiveHubTests()
    {
        _database = TestDatabase.Open(_db.Path);
        _store = RowStore.Open(_database);
    }

    [Fact]
    public async Task A_subscriber_gets_the_rows_it_missed_then_each_new_row_once()
    {
        using var hub = new LiveHub(_store);
        hub.Publish("a", Batch("one"));
        hub.Publish("a", Batch("two"));

        using var live = hub.Subscribe("a", after: 1);
        Assert.Equal([2L], live.Backlog.Select(row => row.Seq));

        var pushed = hub.Publish("a", Batch("three", "four"));
        Assert.Equal([3L, 4L], pushed.Select(row => row.Seq));
        Assert.Equal(3L, (await Next(live)).Seq);
        Assert.Equal(4L, (await Next(live)).Seq);
        Assert.False(live.Rows.TryRead(out _));
    }

    [Fact]
    public async Task A_socket_that_opens_while_rows_are_committing_misses_none_and_repeats_none()
    {
        using var hub = new LiveHub(_store);
        const int total = 400;
        var subscriptions = new List<LiveSubscription>();
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < total; i++)
                hub.Publish("a", Batch($"row {i}"));
        });
        while (!writer.IsCompleted && subscriptions.Count < 40)
        {
            subscriptions.Add(hub.Subscribe("a", after: 0));
            await Task.Yield();
        }
        await writer.WaitAsync(Wait);

        foreach (var live in subscriptions)
        {
            var seqs = live.Backlog.Select(row => row.Seq).ToList();
            while (seqs.Count < total)
                seqs.Add((await Next(live)).Seq);
            Assert.Equal(Enumerable.Range(1, total).Select(n => (long)n), seqs);
            Assert.False(live.Rows.TryRead(out _));
            live.Dispose();
        }
    }

    [Fact]
    public void A_subscriber_sees_only_its_own_session()
    {
        using var hub = new LiveHub(_store);
        hub.Publish("b", Batch("other"));
        using var live = hub.Subscribe("a", after: 0);
        hub.Publish("b", Batch("other again"));

        Assert.Empty(live.Backlog);
        Assert.False(live.Rows.TryRead(out _));
    }

    [Fact]
    public async Task A_socket_that_stops_reading_is_closed_and_the_rest_keep_going()
    {
        using var hub = new LiveHub(_store, queueLimit: 2);
        using var stalled = hub.Subscribe("a", after: 0);
        using var reading = hub.Subscribe("a", after: 0);

        for (var i = 0; i < 3; i++)
        {
            hub.Publish("a", Batch($"row {i}"));
            Assert.Equal(i + 1L, (await Next(reading)).Seq);
        }

        // It still gets the two rows it had room for, then the close.
        var queued = new List<long>();
        await Assert.ThrowsAsync<LiveFellBehind>(async () =>
        {
            await foreach (var row in stalled.Rows.ReadAllAsync().WithCancellation(new CancellationTokenSource(Wait).Token))
                queued.Add(row.Seq);
        });
        Assert.Equal([1L, 2L], queued);
        hub.Publish("a", Batch("row 3"));
        Assert.Equal(4L, (await Next(reading)).Seq);
    }

    [Fact]
    public async Task Closing_a_subscription_ends_its_rows()
    {
        using var hub = new LiveHub(_store);
        var live = hub.Subscribe("a", after: 0);
        live.Dispose();
        hub.Publish("a", Batch("after close"));

        await live.Rows.Completion.WaitAsync(Wait);
        Assert.False(live.Rows.TryRead(out _));
    }

    [Fact]
    public void An_empty_batch_commits_and_pushes_nothing()
    {
        using var hub = new LiveHub(_store);
        using var live = hub.Subscribe("a", after: 0);

        Assert.Empty(hub.Publish("a", new RowBatch()));
        Assert.False(live.Rows.TryRead(out _));
        Assert.Empty(hub.Page("a", long.MaxValue, 50).Rows);
    }

    [Fact]
    public async Task A_copied_row_reaches_sockets_once_like_a_committed_one()
    {
        // On the Mac, a remote session's rows arrive as copies that keep the box's seqs.
        using var boxDb = new TempDatabase();
        using var boxDatabase = TestDatabase.Open(boxDb.Path);
        var box = RowStore.Open(boxDatabase);
        var rows = box.Commit("a", Batch("one", "two"));
        using var hub = new LiveHub(_store);
        using var live = hub.Subscribe("a", after: 0);

        Assert.True(hub.PublishCopy(rows[0]));
        Assert.False(hub.PublishCopy(rows[0]));
        Assert.True(hub.PublishCopy(rows[1]));
        Assert.Equal(1L, (await Next(live)).Seq);
        Assert.Equal(2L, (await Next(live)).Seq);
        Assert.False(live.Rows.TryRead(out _));
        Assert.Equal(2, hub.LastSeq("a"));
    }

    [Fact]
    public async Task Closing_every_subscription_ends_them_and_new_ones_still_open()
    {
        using var hub = new LiveHub(_store);
        var a = hub.Subscribe("a", after: 0);
        var b = hub.Subscribe("b", after: 0);

        hub.CloseSubscriptions();
        await a.Rows.Completion.WaitAsync(Wait);
        await b.Rows.Completion.WaitAsync(Wait);
        using var again = hub.Subscribe("a", after: 0);
        hub.Publish("a", Batch("after"));
        Assert.Equal(1L, (await Next(again)).Seq);
    }

    private static async Task<StoredRow> Next(LiveSubscription live) =>
        await live.Rows.ReadAsync().AsTask().WaitAsync(Wait);

    private static RowBatch Batch(params string[] texts)
    {
        var batch = new RowBatch();
        foreach (var text in texts)
            batch.Rows.Add(new NewRow("run-1", null, RowKind.User, JsonSerializer.SerializeToElement(new { text }), 1));
        return batch;
    }

    public void Dispose()
    {
        _database.Dispose();
        _db.Dispose();
    }
}
