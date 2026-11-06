using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Database;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Infrastructure.Phone;

public sealed record PhoneDevice(string Id, string Name, long CreatedMs, long? LastSeenMs);
public sealed record PhoneMachine(string Id, string Name);

/// <summary>Hash-only Bearer admission for the retained phone HTTP API. All mutations use the host's writer.</summary>
public sealed class PhoneDevices(IAppDatabase database, TopicSet topics, TimeProvider time)
{
    public const string RevokedTopic = "__phone_revoked";

    public PhoneMachine? Machine() => database.QueryFirst("SELECT id,name FROM phone_machine", MachineOf, CancellationToken.None);

    public PhoneMachine NameMachine(string name)
    {
        return database.Writer.Write("phone name machine", scope =>
        {
            scope.Execute("INSERT INTO phone_machine(id,name) SELECT $id,$name WHERE NOT EXISTS(SELECT 1 FROM phone_machine)",
                new("$id", Guid.NewGuid().ToString()), new("$name", name));
            return scope.QueryFirst("UPDATE phone_machine SET name=$name RETURNING id,name", MachineOf, new SqlArg("$name", name))!;
        });
    }

    public (PhoneDevice Device, string Token) Add(string name)
    {
        name = Name(name);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var device = new PhoneDevice(Guid.NewGuid().ToString(), name, Now(), null);
        database.Writer.Write("phone add device", scope => scope.Execute(
            "INSERT INTO phone_devices(id,name,token_hash,created_at) VALUES($id,$name,$hash,$at)",
            new("$id", device.Id), new("$name", device.Name), new("$hash", Hash(token)), new("$at", device.CreatedMs)));
        return (device, token);
    }

    public PhoneDevice? Authenticate(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Hash(token);
        return database.Writer.Write("phone authenticate", scope =>
        {
            var stored = scope.QueryFirst("SELECT token_hash FROM phone_devices WHERE token_hash=$hash AND revoked_at IS NULL",
                row => row.Text(0), new SqlArg("$hash", hash));
            if (stored is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(hash)))
                return null;
            return scope.QueryFirst("""
                UPDATE phone_devices SET last_seen_at=$at WHERE token_hash=$hash AND revoked_at IS NULL
                RETURNING id,name,created_at,last_seen_at
                """, DeviceOf, new("$hash", hash), new("$at", Now()));
        });
    }

    public IReadOnlyList<PhoneDevice> All() => database.Query("""
        SELECT id,name,created_at,last_seen_at FROM phone_devices WHERE revoked_at IS NULL ORDER BY created_at DESC,id
        """, DeviceOf, CancellationToken.None);

    public bool Revoke(string deviceId) => database.Writer.Write("phone revoke device", scope =>
    {
        var changed = scope.Execute("""
            UPDATE phone_devices SET revoked_at=$at,fcm_token=NULL WHERE id=$id AND revoked_at IS NULL
            """, new("$id", deviceId), new("$at", Now())) != 0;
        if (changed) scope.AfterCommit(() => topics.Publish(RevokedTopic, deviceId, "{}"));
        return changed;
    });

    /// <summary>One token belongs to one nonrevoked device; reassociation and clearing commit together.</summary>
    public bool SetPushToken(string deviceId, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.EnumerateRunes().Count() > 4096) throw new ArgumentException("Invalid phone push token.", nameof(token));
        return database.Writer.Write("phone set push token", scope =>
        {
            if (scope.QueryFirst("SELECT id FROM phone_devices WHERE id=$id AND revoked_at IS NULL", row => row.Text(0), new SqlArg("$id", deviceId)) is null)
                return false;
            scope.Execute("UPDATE phone_devices SET fcm_token=CASE WHEN id=$id THEN $token ELSE NULL END WHERE id=$id OR fcm_token=$token",
                new("$id", deviceId), new("$token", token));
            return true;
        });
    }

    public IReadOnlyList<string> PushTokens() => database.Query(
        "SELECT fcm_token FROM phone_devices WHERE revoked_at IS NULL AND fcm_token IS NOT NULL ORDER BY id",
        row => row.Text(0), CancellationToken.None);

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();
    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static PhoneMachine MachineOf(IRow row) => new(row.Text(0), row.Text(1));
    private static PhoneDevice DeviceOf(IRow row) => new(row.Text(0), row.Text(1), row.Long(2), row.IsNull(3) ? null : row.Long(3));
    private static string Name(string name) => !string.IsNullOrWhiteSpace(name) && name.Trim().EnumerateRunes().Count() <= 100
        ? name.Trim() : throw new ArgumentException("The phone name must be 1 to 100 characters.", nameof(name));
}
