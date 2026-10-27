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
/// pruned when the writer opens, and after a write once a day has passed, each prune its own transaction.
/// </summary>
public sealed class AtomicWriter : IAtomicWriter, IDisposable
{
    private static readonly TimeSpan Kept = TimeSpan.FromDays(30);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromDays(1);

    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ITopics _topics;
    private DateTimeOffset? _pruned;

    internal AtomicWriter(string path, TimeProvider time, ITopics topics)
    {
        _time = time;
        _topics = topics;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Sql.Execute(_db, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;", []);
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

            foreach (var (topic, key, json) in scope.Published)
                _topics.Publish(topic, key, json);
            if (_pruned is not { } pruned || _time.GetUtcNow() - pruned >= PruneEvery)
                PruneAfter(what);
            return result;
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _db.Dispose();
    }

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
        var deleted = Sql.Execute(_db, AuditSchema.Prune, [new("$before", (now - Kept).ToUnixTimeMilliseconds())]);
        transaction.Commit();
        _pruned = deleted < AuditSchema.PruneCap ? now : null;
    }

    private sealed class Scope(SqliteConnection db) : IWriteScope
    {
        public List<(string Topic, string? Key, string Json)> Published { get; } = [];

        public int Execute(string sql, params SqlArg[] args) => Sql.Execute(db, sql, args);

        public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args) =>
            Sql.Query(db, sql, map, CancellationToken.None, args);

        public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args) =>
            Query(sql, map, args) is [var first, ..] ? first : default;

        public void Publish<T>(string topic, string? key, T change, JsonTypeInfo<T> type) =>
            Published.Add((topic, key, JsonSerializer.Serialize(change, type)));
    }
}
