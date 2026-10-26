using System.Diagnostics;
using System.Text.Json;
using Assembly.Engine.Database;
using Assembly.Engine.Persistence;
using Assembly.Engine.Rows;
using Assembly.Engine.Store;
using Assembly.Engine.Threads;

namespace Assembly.Tests.Persistence;

public sealed class AppDatabaseTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly AppDatabase _db;

    public AppDatabaseTests()
    {
        _db = AppDatabase.Open(_file.Path);
        _db.Writer.Write("test.schema", scope => scope.Execute("CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT);"));
    }

    [Fact]
    public void A_query_maps_each_row_by_column_position()
    {
        _db.Writer.Write("test.add", scope => scope.Execute("INSERT INTO notes(id, text) VALUES (1, 'a'), (2, NULL);"));

        var notes = _db.Query("SELECT id, text FROM notes ORDER BY id;", row => (row.Long(0), row.IsNull(1) ? null : row.Text(1)),
            CancellationToken.None);

        Assert.Equal([(1L, "a"), (2L, null)], notes);
    }

    [Fact]
    public void A_write_that_throws_leaves_nothing()
    {
        Assert.Throws<InvalidOperationException>(() => _db.Writer.Write<int>("test.fail", scope =>
        {
            scope.Execute("INSERT INTO notes(id, text) VALUES (1, 'a');");
            throw new InvalidOperationException("second statement fails");
        }));

        Assert.Empty(_db.Query("SELECT id FROM notes;", row => row.Long(0), CancellationToken.None));
    }

    [Fact]
    public void A_write_reads_its_own_rows_and_they_are_there_once_it_commits()
    {
        var inside = _db.Writer.Write("test.add", scope =>
        {
            scope.Execute("INSERT INTO notes(id, text) VALUES ($id, $text);", new SqlArg("$id", 7L), new SqlArg("$text", "b"));
            return scope.QueryFirst("SELECT text FROM notes WHERE id = 7;", row => row.Text(0), CancellationToken.None);
        });

        Assert.Equal("b", inside);
        Assert.Equal("b", _db.QueryFirst("SELECT text FROM notes WHERE id = 7;", row => row.Text(0), CancellationToken.None));
    }

    [Fact]
    public async Task A_rows_batch_and_a_thread_write_at_the_same_time_both_commit()
    {
        var rows = RowStore.Open(_db);
        var threads = ThreadStore.Open(_db, TimeProvider.System);
        var payload = JsonSerializer.SerializeToElement(new { text = "x" });
        var batches = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var batch = new RowBatch();
                batch.Rows.Add(new NewRow("run", $"item-{i}", RowKind.Tool, payload, 1));
                rows.Commit("t", batch);
            }
        });
        var created = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                threads.Create("claude", $"repo-{i}");
        });

        await Task.WhenAll(batches, created);

        Assert.Equal(200, rows.LastSeq("t"));
        Assert.Equal(200, threads.List(null, 500).Threads.Count);
    }

    [Fact]
    public void A_cancelled_read_stops_early()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var started = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() => _db.Query(
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT COUNT(*) FROM n;",
            row => row.Long(0), cancel.Token));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"took {started.Elapsed}");
    }

    public void Dispose()
    {
        _db.Dispose();
        _file.Dispose();
    }
}
