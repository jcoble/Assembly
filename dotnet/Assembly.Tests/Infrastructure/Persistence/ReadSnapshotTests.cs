using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Infrastructure.Persistence;

public sealed class ReadSnapshotTests
{
    [Fact]
    public async Task Snapshot_keeps_its_first_view_without_blocking_writer()
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        db.Writer.Write("seed", s => s.Execute("CREATE TABLE snapshot_test(id INTEGER); INSERT INTO snapshot_test VALUES(1)"));
        using var read = db.OpenSnapshot(default);
        Assert.Equal(1, read.QueryFirst("SELECT count(*) FROM snapshot_test", r => r.Long(0)));
        await Task.Run(() => db.Writer.Write("add", s => s.Execute("INSERT INTO snapshot_test VALUES(2)"))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, read.QueryFirst("SELECT count(*) FROM snapshot_test", r => r.Long(0)));
        Assert.ThrowsAny<Exception>(() => read.Query("INSERT INTO snapshot_test VALUES(3)", r => r.Long(0)));
    }

    [Fact]
    public async Task Cancellation_interrupts_active_read_and_disposal_ends_snapshot()
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        using var cancel = new CancellationTokenSource();
        var read = db.OpenSnapshot(cancel.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = Task.Run(() => read.Query(
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n) SELECT i FROM n",
            r => { entered.TrySetResult(); return r.Long(0); }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.WaitAsync(TimeSpan.FromSeconds(5)));
        read.Dispose();
        read.Dispose();
        Assert.Throws<ObjectDisposedException>(() => read.Query("SELECT 1", r => r.Long(0)));
        using var next = db.OpenSnapshot(default);
        Assert.Equal(1, next.QueryFirst("SELECT 1", r => r.Long(0)));
    }
}
