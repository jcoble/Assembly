using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Preferences;

/// <summary>The app's preferences, in the host's SQLite file: reads through the database, saves through its one writer.</summary>
internal sealed class PreferenceStore
{
    private readonly IAppDatabase _db;

    public PreferenceStore(IAppDatabase db)
    {
        _db = db;
        _db.Writer.Write("preferences.schema", scope => scope.Execute(PreferencesSchema.Create));
    }

    /// <summary>Every saved preference, its value as JSON text. Stops when the token is cancelled.</summary>
    public Task<IReadOnlyList<(string Key, string Value)>> ReadAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(string, string)>>(
            _db.Query("SELECT key, value FROM preferences ORDER BY key;", row => (row.Text(0), row.Text(1)), cancellationToken));

    /// <summary>One saved preference, its value as JSON text; null when it isn't saved.</summary>
    public string? Read(string key) =>
        _db.QueryFirst("SELECT value FROM preferences WHERE key = $key;", row => row.Text(0), CancellationToken.None, new SqlArg("$key", key));

    /// <summary>
    /// Saves each of the object's properties under its name, in one transaction; other keys stay. It takes no token:
    /// a save that has started always finishes.
    /// </summary>
    public void Save(JsonElement values) =>
        _db.Writer.Write("preferences.set", scope =>
        {
            foreach (var value in values.EnumerateObject())
                scope.Execute("INSERT INTO preferences(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = $value;",
                    new SqlArg("$key", value.Name), new SqlArg("$value", value.Value.GetRawText()));
            return 0;
        });
}
