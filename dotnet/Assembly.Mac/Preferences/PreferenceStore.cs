using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Assembly.Mac.Preferences;

/// <summary>
/// The app's preferences, in the host's SQLite file. Saves are rare (the page batches them), so each call opens its
/// own connection.
/// </summary>
internal sealed class PreferenceStore
{
    private readonly string _database;

    public PreferenceStore(string database)
    {
        _database = database;
        using var db = Open();
        Execute(db, PreferencesSchema.Create);
    }

    /// <summary>Every saved preference, its value as JSON text. Stops when the token is cancelled.</summary>
    public async Task<IReadOnlyList<(string Key, string Value)>> ReadAllAsync(CancellationToken cancellationToken)
    {
        await using var db = Open();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT key, value FROM preferences ORDER BY key;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var all = new List<(string, string)>();
        while (await reader.ReadAsync(cancellationToken))
            all.Add((reader.GetString(0), reader.GetString(1)));
        return all;
    }

    /// <summary>
    /// Saves each of the object's properties under its name, in one transaction; other keys stay. It takes no token:
    /// a save that has started always finishes (the page debounces saves instead of cancelling them).
    /// </summary>
    public void Save(JsonElement values)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        foreach (var value in values.EnumerateObject())
        {
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO preferences(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = $value;";
            command.Parameters.AddWithValue("$key", value.Name);
            command.Parameters.AddWithValue("$value", value.Value.GetRawText());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
