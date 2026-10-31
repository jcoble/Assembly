using Assembly.Application.Common.Database;
using Microsoft.Data.Sqlite;

namespace Assembly.Infrastructure.Persistence;

/// <summary>Owns one pooled read-only connection and its deferred read transaction.</summary>
public sealed class ReadSnapshot : IReadSnapshot
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly CancellationToken _cancel;
    private bool _disposed;

    internal ReadSnapshot(string connectionString, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        _cancel = cancel;
        _connection = new(connectionString);
        try
        {
            _connection.Open();
            _transaction = _connection.BeginTransaction(deferred: true);
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Sql.Query(_connection, sql, map, _cancel, args);
    }

    public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args) =>
        Query(sql, map, args) is [var first, ..] ? first : default;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transaction.Dispose(); }
        finally { _connection.Dispose(); }
    }
}
