using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Infrastructure.Persistence;

/// <summary>The writer's checkpoints (plan B3b), on a real temporary SQLite file: none inside a commit, the WAL
/// rewinds in a quiet moment, and past the cap it rewinds under steady writes too.</summary>
public sealed class CheckpointTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private AtomicWriter? _writer;
    private int _id;

    public void Dispose()
    {
        _writer?.Dispose();
        _file.Dispose();
    }

    [Fact]
    public void The_write_connection_never_checkpoints_inside_a_commit()
    {
        var writer = Open();

        Assert.Equal(0, writer.Write("test.pragma", scope => scope.QueryFirst("PRAGMA wal_autocheckpoint;", row => row.Long(0))));
    }

    [Fact]
    public void A_checkpoint_still_runs_after_enough_commits()
    {
        Open();
        var before = new FileInfo(_file.Path).Length;

        Notes(100);

        // The checkpoint copies the WAL into the main file, after the write, on its own connection.
        Assert.True(SpinWait.SpinUntil(() => new FileInfo(_file.Path).Length > before, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void The_wal_rewinds_once_writes_pause()
    {
        Open();
        Notes(150);
        var round = WalRound();

        Assert.True(Until(() => WalRound() != round, gap: 20, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Steady_writes_rewind_again_and_again_once_past_the_cap(int gap)
    {
        Open(walCap: 300);
        var round = WalRound();
        var rewinds = 0;

        for (var i = 0; i < 3_000; i++)
        {
            Note();
            if (gap > 0)
                Thread.Sleep(gap);
            if (WalRound() != round)
                (rewinds, round) = (rewinds + 1, WalRound());
        }

        // Without the cap these writes never pause long enough for a rewind, and the WAL passes 6,000 pages. The peak
        // with it, the cap plus what one background checkpoint's passes let in, depends on the disk's fsync, so it's
        // in the plan's measurement rather than here.
        Assert.True(rewinds >= 2, $"{rewinds} rewinds");
    }

    [Fact]
    public void A_reader_holding_a_snapshot_stops_the_rewind_but_not_the_commits()
    {
        Open();
        Notes(10);
        var round = WalRound();
        var slowest = TimeSpan.Zero;

        using (var reader = new SqliteConnection($"Data Source={_file.Path};Pooling=False"))
        {
            reader.Open();
            using var snapshot = reader.BeginTransaction(deferred: true);
            using (var read = reader.CreateCommand())
            {
                read.CommandText = "SELECT COUNT(*) FROM notes;";
                read.ExecuteScalar();
            }

            for (var i = 0; i < 300; i++)
            {
                var watch = Stopwatch.StartNew();
                Note();
                slowest = TimeSpan.FromTicks(Math.Max(slowest.Ticks, watch.Elapsed.Ticks));
            }

            Thread.Sleep(100);
            Note();
            Assert.Equal(round, WalRound());
        }

        // A commit that waited on the reader would take SQLite's busy timeout, seconds rather than milliseconds.
        Assert.True(slowest < TimeSpan.FromMilliseconds(500), $"slowest commit {slowest.TotalMilliseconds} ms");
        // With the reader gone, the next checkpoint (at most 100 commits away) copies everything.
        Notes(100);
        Assert.True(Until(() => WalRound() != round, gap: 20, TimeSpan.FromSeconds(1)));
    }

    private AtomicWriter Open(int walCap = AtomicWriter.WalCap)
    {
        _writer = new AtomicWriter(_file.Path, TimeProvider.System, new global::Assembly.Infrastructure.Topics.Topics(), walCap);
        _writer.Write("test.schema", scope => scope.Execute("CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT);"));
        return _writer;
    }

    private void Note() =>
        _writer!.Write("notes/add", scope => scope.Execute("INSERT INTO notes(id, text) VALUES ($id, 'n');", new SqlArg("$id", (long)++_id)));

    private void Notes(int count)
    {
        for (var i = 0; i < count; i++)
            Note();
    }

    // Writes a note every gap milliseconds until done is true or the time runs out.
    private bool Until(Func<bool> done, int gap, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < limit)
        {
            Thread.Sleep(gap);
            Note();
            if (done())
                return true;
        }
        return false;
    }

    // The WAL header's checkpoint sequence number, which SQLite bumps each time it rewinds the WAL.
    private uint WalRound()
    {
        using var wal = new FileStream(_file.Path + "-wal", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[16];
        wal.ReadExactly(header);
        return BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
    }

}
