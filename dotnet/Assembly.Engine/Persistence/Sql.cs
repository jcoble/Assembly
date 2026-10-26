using Microsoft.Data.Sqlite;
using SQLitePCL;
using Assembly.Engine.Database;

namespace Assembly.Engine.Persistence;

/// <summary>Runs SQL on a connection: the reads and the executes both <see cref="AppDatabase"/> and the writer use.</summary>
internal static class Sql
{
    public static List<T> Query<T>(SqliteConnection db, string sql, Func<IRow, T> map, CancellationToken cancel, SqlArg[] args)
    {
        cancel.ThrowIfCancellationRequested();
        using var command = Command(db, sql, args);
        // Cancelling interrupts the connection in SQLite, so a long read stops instead of running to the end.
        // SqliteCommand.Cancel does nothing, so this goes to SQLite itself.
        using var stop = cancel.Register(() => raw.sqlite3_interrupt(db.Handle));
        try
        {
            using var reader = command.ExecuteReader();
            var row = new SqliteRow(reader);
            var results = new List<T>();
            while (reader.Read())
                results.Add(map(row));
            return results;
        }
        catch (SqliteException) when (cancel.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancel);
        }
    }

    public static int Execute(SqliteConnection db, string sql, SqlArg[] args)
    {
        using var command = Command(db, sql, args);
        return command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, SqlArg[] args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var arg in args)
            command.Parameters.AddWithValue(arg.Name, arg.Value ?? DBNull.Value);
        return command;
    }
}
