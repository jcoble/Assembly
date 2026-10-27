using System.Buffers.Binary;
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
        var round = WalRound();

        NextCheckpoint();
        Note();

        Assert.NotEqual(round, WalRound());
    }

    [Fact]
    public void Past_the_cap_a_commit_copies_the_wal_so_the_next_one_rewinds_again_and_again()
    {
        Open(walCap: 300);

        // A reader's snapshot keeps the background checkpoints from copying the WAL, which is what steady writes do,
        // without depending on the disk's speed. Each one is waited for, so the writer always sees how it ended. Once
        // one ends with the WAL past the cap, every commit copies the WAL itself instead of starting another, so 150
        // commits with no new checkpoint means the cap has taken over. Then the reader lets go, and with no checkpoint
        // running only the cap can copy the WAL: the first commit does, and the second starts with every frame copied
        // and rewinds it. Without the cap a checkpoint starts every 100 commits and nothing copies the WAL.
        for (var time = 0; time < 2; time++)
        {
            using (var reader = new SqliteConnection($"Data Source={_file.Path};Pooling=False"))
            {
                reader.Open();
                using var snapshot = reader.BeginTransaction(deferred: true);
                using (var read = reader.CreateCommand())
                {
                    read.CommandText = "SELECT COUNT(*) FROM notes;";
                    read.ExecuteScalar();
                }
                var since = 0;
                for (var notes = 0; since < 150; notes++, since++)
                {
                    Assert.True(notes < 5_000, "the cap never took over from the background checkpoints");
                    var checkpoint = _writer!.Running;
                    Settle();
                    Note();
                    if (_writer.Running != checkpoint)
                        since = 0;
                }
            }

            var round = WalRound();
            Note();
            Assert.Equal(round, WalRound());
            Note();
            Assert.NotEqual(round, WalRound());
        }
    }

    [Fact]
    public void A_reader_holding_a_snapshot_stops_the_rewind_but_not_the_commits()
    {
        Open();
        Notes(10);
        var round = WalRound();

        using (var reader = new SqliteConnection($"Data Source={_file.Path};Pooling=False"))
        {
            reader.Open();
            using var snapshot = reader.BeginTransaction(deferred: true);
            using (var read = reader.CreateCommand())
            {
                read.CommandText = "SELECT COUNT(*) FROM notes;";
                read.ExecuteScalar();
            }

            // The commits go on while the snapshot is open; one that waited on the reader would time out and throw.
            NextCheckpoint();
            Note();
            Assert.Equal(round, WalRound());
        }

        NextCheckpoint();
        Note();
        Assert.NotEqual(round, WalRound());
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

    // Writes notes until the writer starts its next background checkpoint, then waits for it to finish.
    private void NextCheckpoint()
    {
        var last = _writer!.Running;
        while (_writer.Running == last)
            Note();
        Assert.True(_writer.Running!.Wait(TimeSpan.FromSeconds(30)));
    }

    // Waits for a background checkpoint still running, so the next commit sees how it ended.
    private void Settle()
    {
        if (_writer!.Running is { IsCompleted: false } running)
            Assert.True(running.Wait(TimeSpan.FromSeconds(30)));
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
