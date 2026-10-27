using Microsoft.Data.Sqlite;
using Assembly.Application.Common.Database;

namespace Assembly.Infrastructure.Persistence;

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
        CopyBeforeRename(path);
        // The writer opens first: it makes the file and turns WAL on, which read-only connections can't.
        _writer = new AtomicWriter(path);
        _reads = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = true }.ToString();
    }

    /// <summary>
    /// A database from before sessions were renamed (format 3 or older, with a threads table) is copied to
    /// <c>path.before-v4</c> before anything changes it. A copy already there is never overwritten. The copy is written
    /// beside it and renamed into place once whole, so a half-written one is made again. If the copy fails, this throws
    /// and the database is left as it was.
    /// </summary>
    private static void CopyBeforeRename(string path)
    {
        var copy = path + ".before-v4";
        if (!File.Exists(path) || File.Exists(copy))
            return;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT user_version < 4 AND EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'threads') FROM pragma_user_version;";
        if ((long)command.ExecuteScalar()! == 0)
            return;
        var partial = copy + ".partial";
        File.Delete(partial);
        command.CommandText = "VACUUM INTO $copy;";
        command.Parameters.AddWithValue("$copy", partial);
        command.ExecuteNonQuery();
        // SQLite doesn't sync VACUUM INTO's output: sync it, so a crash right after the rename can't leave a cut-off copy.
        using (var written = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite))
            written.Flush(flushToDisk: true);
        File.Move(partial, copy);
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
