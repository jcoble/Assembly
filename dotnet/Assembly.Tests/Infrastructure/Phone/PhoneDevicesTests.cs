using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Phone;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Phone;

public sealed class PhoneDevicesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phone-devices-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new();
    private readonly TopicSet _topics = new();
    private readonly AppDatabase _database;
    private readonly PhoneDevices _devices;

    public PhoneDevicesTests()
    {
        _database = AppDatabase.Open(_path, _time, _topics);
        PhoneSchema.Open(_database);
        _devices = new(_database, _topics, _time);
    }

    [Fact]
    public void A_new_device_has_a_unique_bearer_but_only_its_hash_is_stored()
    {
        var (first, token) = _devices.Add(" Phone ");
        var (second, other) = _devices.Add("Other");
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(token, other);
        Assert.Equal("Phone", first.Name);
        Assert.Null(first.LastSeenMs);
        Assert.Equal(32, Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=").Length);
        var hashes = _database.Query("SELECT token_hash FROM phone_devices", row => row.Text(0), default);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), hashes);
        Assert.DoesNotContain(token, hashes);
        Assert.All(_database.Query("SELECT what FROM audit", row => row.Text(0), default), entry => Assert.DoesNotContain(token, entry));
    }

    [Fact]
    public void Authentication_marks_only_the_matching_nonrevoked_device_seen()
    {
        var (device, token) = _devices.Add("Phone");
        var (other, _) = _devices.Add("Other");
        _time.Advance(TimeSpan.FromMinutes(1));
        var authenticated = Assert.IsType<PhoneDevice>(_devices.Authenticate(token));
        Assert.Equal(device.Id, authenticated.Id);
        Assert.Equal(_time.GetUtcNow().ToUnixTimeMilliseconds(), authenticated.LastSeenMs);
        Assert.Null(_devices.Authenticate("unknown"));
        Assert.Null(_devices.Authenticate(""));
        Assert.Null(_devices.All().Single(item => item.Id == other.Id).LastSeenMs);
        Assert.True(_devices.Revoke(device.Id));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(_devices.Authenticate(token));
        Assert.Equal(authenticated.LastSeenMs, _database.QueryFirst(
            "SELECT last_seen_at FROM phone_devices WHERE id=$id", row => (long?)row.Long(0), default, new SqlArg("$id", device.Id)));
    }

    [Fact]
    public void Revocation_notifies_after_commit_once_and_leaves_other_devices_admitted()
    {
        var (device, token) = _devices.Add("Phone");
        var (_, otherToken) = _devices.Add("Other");
        _devices.SetPushToken(device.Id, "push-canary");
        var notices = new List<string?>();
        using var subscription = _topics.Subscribe(PhoneDevices.RevokedTopic, null, _ => { }, (id, json) =>
        {
            Assert.Equal(device.Id, id);
            Assert.Equal("{}", json);
            Assert.Empty(_devices.PushTokens());
            Assert.DoesNotContain(_devices.All(), item => item.Id == id);
            notices.Add(id);
        });
        Assert.True(_devices.Revoke(device.Id));
        Assert.False(_devices.Revoke(device.Id));
        Assert.Single(notices);
        Assert.Null(_devices.Authenticate(token));
        Assert.NotNull(_devices.Authenticate(otherToken));
    }

    [Fact]
    public void A_failed_revocation_rolls_back_token_and_push_state_without_a_disconnect_notice()
    {
        var (device, token) = _devices.Add("Phone");
        _devices.SetPushToken(device.Id, "push-canary");
        var notices = 0;
        using var subscription = _topics.Subscribe(PhoneDevices.RevokedTopic, null, _ => { }, (_, _) => notices++);
        _database.Writer.Write("test reject revocation", scope => scope.Execute("""
            CREATE TRIGGER reject_revoke BEFORE UPDATE OF revoked_at ON phone_devices
            BEGIN SELECT RAISE(ABORT,'test refused'); END;
            """));
        Assert.Throws<SqliteException>(() => _devices.Revoke(device.Id));
        Assert.Equal(0, notices);
        Assert.NotNull(_devices.Authenticate(token));
        Assert.Equal(["push-canary"], _devices.PushTokens());
    }

    [Fact]
    public void Reopening_and_renaming_preserves_the_existing_phone_machine_and_device_identity()
    {
        Assert.Null(_devices.Machine());
        var machine = _devices.NameMachine("First");
        var (device, token) = _devices.Add("Phone");
        using var reopened = AppDatabase.Open(_path, _time, _topics);
        PhoneSchema.Open(reopened);
        var devices = new PhoneDevices(reopened, _topics, _time);
        Assert.Equal(new PhoneMachine(machine.Id, "Renamed"), devices.NameMachine("Renamed"));
        Assert.Equal(machine.Id, devices.Machine()!.Id);
        Assert.Equal(device.Id, devices.Authenticate(token)!.Id);
        Assert.Single(devices.All());
    }

    [Fact]
    public void A_push_token_moves_atomically_to_one_device_and_cannot_be_given_to_a_revoked_device()
    {
        var (first, _) = _devices.Add("First");
        var (second, _) = _devices.Add("Second");
        Assert.True(_devices.SetPushToken(first.Id, "push-canary"));
        Assert.True(_devices.SetPushToken(second.Id, "push-canary"));
        Assert.Equal(["push-canary"], _devices.PushTokens());
        Assert.Null(_database.QueryFirst("SELECT fcm_token FROM phone_devices WHERE id=$id",
            row => row.IsNull(0) ? null : row.Text(0), default, new SqlArg("$id", first.Id)));
        _devices.Revoke(second.Id);
        Assert.False(_devices.SetPushToken(second.Id, "replacement"));
        Assert.False(_devices.SetPushToken("unknown", "replacement"));
        Assert.Empty(_devices.PushTokens());
    }

    public void Dispose()
    {
        _database.Dispose();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" }) File.Delete(file);
    }
}
