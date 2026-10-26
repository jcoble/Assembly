using Microsoft.Data.Sqlite;
using Assembly.Engine.Database;

namespace Assembly.Engine.Persistence;

/// <summary>
/// One SQLite file: its reads, and the one <see cref="AtomicWriter"/> every write to it goes through. Each read takes
/// a pooled read-only connection of its own, so a slow read never holds up another and never touches the write
/// connection.
/// </summary>
public sealed class AppDatabase : IAppDatabase, IDisposable
{
    private readonly AtomicWriter _writer;
    private readonly string _reads;

    private AppDatabase(string path)
    {
        // The writer opens first: it makes the file and turns WAL on, which read-only connections can't.
        _writer = new AtomicWriter(path);
        _reads = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = true }.ToString();
    }

    public IAtomicWriter Writer => _writer;

    public static AppDatabase Open(string path) => new(path);

    public List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args)
    {
        using var db = new SqliteConnection(_reads);
        db.Open();
        return Sql.Query(db, sql, map, cancel, args);
    }

    public T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) =>
        Query(sql, map, cancel, args) is [var first, ..] ? first : default;

    public void Dispose()
    {
        _writer.Dispose();
        using var db = new SqliteConnection(_reads);
        SqliteConnection.ClearPool(db);
    }
}
