namespace Assembly.Application.Features.Preferences;

/// <summary>The preferences' table: one row per key, its value as JSON text.</summary>
internal static class PreferencesSchema
{
    public const string Create = "CREATE TABLE IF NOT EXISTS preferences(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
}
