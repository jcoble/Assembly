using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;

namespace Assembly.Desktop.Settings;

/// <summary>
/// Persists each provider's on/off state in <c>assembly.db</c>. Every provider already installed counts as switched on
/// once detected; explicit off settings stay off across restarts (the UI switch comes in a later ticket).
/// All writes are single-transaction and publish only after commit.
/// </summary>
internal sealed class ProviderEnablementStore
{
    internal const string Topic = "__settings_providers_enabled";
    private const string CurrentKey = "current";

    private const string CreateTable = """
        CREATE TABLE IF NOT EXISTS settings_provider_enabled (
            provider TEXT PRIMARY KEY,
            enabled INTEGER NOT NULL,
            updated_ms INTEGER NOT NULL
        );
        """;

    private readonly IAppDatabase _db;
    private readonly TimeProvider _time;

    internal ProviderEnablementStore(IAppDatabase db, TimeProvider time)
    {
        _db = db;
        _time = time;
        EnsureSchema(db);
    }

    internal static void EnsureSchema(IAppDatabase db) =>
        db.Writer.Write("settings.providers.schema", scope => scope.Execute(CreateTable));

    internal IReadOnlyDictionary<string, bool> ReadAll(CancellationToken ct)
    {
        using var snapshot = _db.OpenSnapshot(ct);
        return ReadAll(snapshot);
    }

    internal static IReadOnlyDictionary<string, bool> ReadAll(IReadSnapshot snapshot)
    {
        var rows = snapshot.Query(
            "SELECT provider, enabled FROM settings_provider_enabled;",
            row => (Provider: row.Text(0), Enabled: row.Long(1) != 0));
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (provider, enabled) in rows)
            map[provider] = enabled;
        return map;
    }

    /// <summary>
    /// Marks every detected installed provider as switched on when it has no saved on/off setting yet.
    /// Existing rows (including explicit off settings) are preserved. Publishes after commit when any row is added.
    /// </summary>
    internal bool EnsureEnabled(IEnumerable<string> installedProviders)
    {
        var candidates = installedProviders
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
            return false;

        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        return _db.Writer.Write("settings.providers.seed", scope =>
        {
            var existing = new HashSet<string>(
                scope.Query("SELECT provider FROM settings_provider_enabled;", row => row.Text(0)),
                StringComparer.Ordinal);
            var added = false;
            foreach (var provider in candidates)
            {
                if (!existing.Add(provider))
                    continue;
                scope.Execute(
                    "INSERT INTO settings_provider_enabled(provider, enabled, updated_ms) VALUES ($provider, 1, $now) ON CONFLICT(provider) DO NOTHING;",
                    new SqlArg("$provider", provider),
                    new SqlArg("$now", nowMs));
                added = true;
            }
            if (added)
                scope.Publish(Topic, CurrentKey, new ProviderEnablementChanged(nowMs), ProviderEnablementJson.Default.ProviderEnablementChanged);
            return added;
        });
    }

    /// <summary>Atomically saves one provider's on/off setting and publishes after commit.</summary>
    internal void SetEnabled(string provider, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return;
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _db.Writer.Write("settings.providers.set", scope =>
        {
            scope.Execute(
                """
                INSERT INTO settings_provider_enabled(provider, enabled, updated_ms)
                VALUES ($provider, $enabled, $now)
                ON CONFLICT(provider) DO UPDATE SET enabled = excluded.enabled, updated_ms = excluded.updated_ms;
                """,
                new SqlArg("$provider", provider),
                new SqlArg("$enabled", enabled ? 1L : 0L),
                new SqlArg("$now", nowMs));
            scope.Publish(Topic, CurrentKey, new ProviderEnablementChanged(nowMs), ProviderEnablementJson.Default.ProviderEnablementChanged);
            return 0;
        });
    }
}

internal sealed record ProviderEnablementChanged(long UpdatedMs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProviderEnablementChanged))]
internal sealed partial class ProviderEnablementJson : JsonSerializerContext;
