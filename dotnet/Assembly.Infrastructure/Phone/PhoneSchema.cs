using Assembly.Application.Common.Database;

namespace Assembly.Infrastructure.Phone;

/// <summary>The retained HTTP phone identity and paired-device store, separate from Engine ownership.</summary>
public static class PhoneSchema
{
    public static void Open(IAppDatabase database) => database.Writer.Write("phone schema", scope =>
    {
        scope.Execute("""
            CREATE TABLE IF NOT EXISTS phone_machine (
                id TEXT PRIMARY KEY, name TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS phone_devices (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE,
                fcm_token TEXT, created_at INTEGER NOT NULL, last_seen_at INTEGER, revoked_at INTEGER
            );
            """);
        return true;
    });
}
