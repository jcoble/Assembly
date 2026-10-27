namespace Assembly.Infrastructure.Persistence;

/// <summary>One row per write: when, and what (a call's <c>feature/method</c>, or the store's label). Pruned after 30 days.</summary>
internal static class AuditSchema
{
    public const string Create = """
        CREATE TABLE IF NOT EXISTS audit(at INTEGER NOT NULL, what TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS audit_at ON audit(at);
        """;

    public const string Add = "INSERT INTO audit(at, what) VALUES ($at, $what);";

    // At most PruneCap rows a prune, so it stays short for the write waiting behind it.
    public const int PruneCap = 5_000;

    public const string Prune = "DELETE FROM audit WHERE rowid IN (SELECT rowid FROM audit WHERE at < $before LIMIT 5000);";
}
