using Microsoft.Data.Sqlite;
using Assembly.Application.Common.Database;

namespace Assembly.Infrastructure.Persistence;

/// <summary>
/// Every write to one database file, and the only place a transaction begins. One long-lived write connection behind
/// one lock, held for the whole write: each write is one transaction, committed whole, or rolled back if its body
/// throws. A plain lock is enough because the body is synchronous.
/// </summary>
public sealed class AtomicWriter : IAtomicWriter, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    internal AtomicWriter(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Sql.Execute(_db, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;", []);
    }

    public T Write<T>(string what, Func<IWriteScope, T> write)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            var result = write(new Scope(_db));
            transaction.Commit();
            return result;
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _db.Dispose();
    }

    private sealed class Scope(SqliteConnection db) : IWriteScope
    {
        public int Execute(string sql, params SqlArg[] args) => Sql.Execute(db, sql, args);

        public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args) =>
            Sql.Query(db, sql, map, CancellationToken.None, args);

        public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args) =>
            Query(sql, map, args) is [var first, ..] ? first : default;
    }
}
