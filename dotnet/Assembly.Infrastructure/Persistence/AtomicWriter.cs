using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Topics;

namespace Assembly.Infrastructure.Persistence;

/// <summary>
/// Every write to one database file, and the only place a transaction begins. One long-lived write connection behind
/// one lock, held for the whole write: each write is one transaction with its audit row, committed whole, or rolled
/// back if its body throws. A plain lock is enough because the body is synchronous. After the commit, still under the
/// lock, the write's publishes go to the topics, so they go out in commit order. Audit rows older than 30 days are
/// pruned when the writer opens, and after a write once a day has passed, each prune its own transaction. SQLite's
/// automatic checkpoint is off, because it runs inside the commit that crosses 1,000 WAL pages: every 100 commits the
/// writer starts one passive checkpoint on its own connection instead, outside the lock. Past a cap on the WAL, a write
/// finishes the copy itself after its commit, under the lock.
/// </summary>
public sealed class AtomicWriter : IAtomicWriter, IDisposable
{
    private static readonly TimeSpan Kept = TimeSpan.FromDays(30);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromDays(1);
    // Most commits are one to a few pages, so 100 keeps the WAL near the 1,000 pages SQLite's own checkpoint allows.
    private const int CheckpointEvery = 100;
    private const int CheckpointPasses = 5;
    // About 64 MB of 4 KB pages. SQLite rewinds the WAL only for a write that starts with every frame copied, and the
    // copy ends in an fsync, so steady writes with no pause that long would grow the WAL without bound. Past the cap a
    // write does that copy itself: one slower commit (10 to 20 ms on agent-workbox), only on a long write-heavy turn.
    internal const int WalCap = 16_384;

    private readonly SqliteConnection _db;
    private readonly SqliteConnection _checkpointDb;
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ITopics _topics;
    private DateTimeOffset? _pruned;
    private int _commits;
    private Task? _checkpoint;
    private readonly int _walCap;
    // The WAL's frames as the last checkpoint found them, set by the background checkpoint and read under the lock.
    private long _walFrames;

    internal AtomicWriter(string path, TimeProvider time, ITopics topics, int walCap = WalCap)
    {
        _walCap = walCap;
        _time = time;
        _topics = topics;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Sql.Execute(_db, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA wal_autocheckpoint = 0;", []);
        _checkpointDb = new SqliteConnection(_db.ConnectionString);
        _checkpointDb.Open();
        Sql.Execute(_db, AuditSchema.Create, []);
        lock (_gate)
            Prune();
    }

    public T Write<T>(string what, Func<IWriteScope, T> write)
    {
        lock (_gate)
        {
            var scope = new Scope(_db);
            T result;
            using (var transaction = _db.BeginTransaction())
            {
                result = write(scope);
                // An async body would run on after Write returns, outside the transaction and the lock.
                if (result is Task)
                    throw new InvalidOperationException($"{what}: a write's body must be synchronous.");
                Sql.Execute(_db, AuditSchema.Add, [new("$at", _time.GetUtcNow().ToUnixTimeMilliseconds()), new("$what", what)]);
                transaction.Commit();
            }

            foreach (var (topic, key, json, keyedOnly) in scope.Published)
                _topics.Publish(topic, key, json, keyedOnly);
            // One decision, so a new background checkpoint never beats the cap's catch-up to the moment the last ends.
            _commits++;
            if (_checkpoint is not { IsCompleted: false })
            {
                if (Volatile.Read(ref _walFrames) >= _walCap)
                    CatchUp();
                else if (_commits >= CheckpointEvery)
                {
                    _commits = 0;
                    _checkpoint = Task.Run(Checkpoint);
                }
            }
            if (_pruned is not { } pruned || _time.GetUtcNow() - pruned >= PruneEvery)
                PruneAfter(what);
            return result;
        }
    }

    // The background checkpoint, if one has started, for tests to wait on.
    internal Task? Running => _checkpoint;

    public void Dispose()
    {
        lock (_gate)
        {
            // Waiting under the lock is safe only because a checkpoint never takes it.
            _checkpoint?.Wait();
            _checkpointDb.Dispose();
            _db.Dispose();
        }
    }

    // Passive: it copies what no reader still needs and never waits, so a reader holding a snapshot only leaves it
    // incomplete. SQLite rewinds the WAL only for a write that starts with every frame copied, so while frames keep
    // arriving the pass runs again, at most a few times, to catch up in the next quiet moment.
    private void Checkpoint()
    {
        try
        {
            long copied = -1, frames = -1;
            for (var pass = 0; pass < CheckpointPasses; pass++)
            {
                var before = frames;
                (frames, copied) = Pass(_checkpointDb);
                if (copied == frames && frames == before)
                    break;
            }
            Volatile.Write(ref _walFrames, frames);
        }
        catch (SqliteException error)
        {
            Console.Error.WriteLine($"checkpoint failed: {error.Message}");
        }
    }

    // Under the lock, after this write's commit, so no frame arrives during it and the next write rewinds the WAL,
    // unless a reader still needs it.
    private void CatchUp()
    {
        try
        {
            var (frames, copied) = Pass(_db);
            _walFrames = copied == frames ? 0 : frames;
        }
        catch (SqliteException error)
        {
            Console.Error.WriteLine($"checkpoint catch-up failed: {error.Message}");
        }
    }

    private static (long Frames, long Copied) Pass(SqliteConnection db) =>
        Sql.Query(db, "PRAGMA wal_checkpoint(PASSIVE);", row => (row.Long(1), row.Long(2)), CancellationToken.None, [])[0];

    // A failed prune leaves the write before it committed; the next write tries again.
    private void PruneAfter(string what)
    {
        try
        {
            Prune();
        }
        catch (SqliteException error)
        {
            Console.Error.WriteLine($"audit prune after {what} failed: {error.Message}");
        }
    }

    // One capped delete. Hitting the cap leaves the day's prune not done, so a backlog clears over the next writes.
    private void Prune()
    {
        var now = _time.GetUtcNow();
        using var transaction = _db.BeginTransaction();
        var deleted = Sql.Execute(_db, AuditSchema.Prune, [new("$before", (now - Kept).ToUnixTimeMilliseconds()), new("$cap", (long)AuditSchema.PruneCap)]);
        transaction.Commit();
        _pruned = deleted < AuditSchema.PruneCap ? now : null;
    }

    private sealed class Scope(SqliteConnection db) : IWriteScope
    {
        public List<(string Topic, string? Key, string Json, bool KeyedOnly)> Published { get; } = [];

        public int Execute(string sql, params SqlArg[] args) => Sql.Execute(db, sql, args);

        public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args) =>
            Sql.Query(db, sql, map, CancellationToken.None, args);

        public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args) =>
            Query(sql, map, args) is [var first, ..] ? first : default;

        public void Publish<T>(string topic, string? key, T change, JsonTypeInfo<T> type, bool keyedOnly = false) =>
            Published.Add((topic, key, JsonSerializer.Serialize(change, type), keyedOnly));
    }
}
