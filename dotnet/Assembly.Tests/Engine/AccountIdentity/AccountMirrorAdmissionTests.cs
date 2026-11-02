using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mirror;
using Assembly.Engine.Reading;

namespace Assembly.Tests.Engine.AccountIdentity;

public sealed class AccountMirrorAdmissionTests
{
    [Theory]
    [InlineData("provider", false)]
    [InlineData("provider", true)]
    [InlineData("sign_in", false)]
    [InlineData("sign_in", true)]
    public void Incoming_row_rejects_private_or_noncanonical_account_before_storage(string kind, bool uppercase)
    {
        using var f = new AccountIdentityFixture(seed: false); Open(f);
        var before = f.Digest(); var account = uppercase ? "acct-v1:" + new string('A', 64) : AccountIdentityFixture.RawCanary;
        var error = Assert.Throws<ArgumentException>(() => f.Core.Db.Writer.Write("test.invalid-mirror-account", s => { MirrorApply.SnapshotPage(s, "peer", [Row(kind, account)]); return 0; }));
        Assert.Equal("Invalid mirror row", error.Message); Assert.Equal(before, f.Digest());
    }

    [Fact]
    public void Incoming_provider_key_must_match_its_exact_stored_pair()
    {
        using var f = new AccountIdentityFixture(seed: false); Open(f); var before = f.Digest();
        Assert.Throws<ArgumentException>(() => f.Core.Db.Writer.Write("test.invalid-mirror-key", s => { MirrorApply.SnapshotPage(s, "peer", [Row("provider", AccountIdentityFixture.Opaque, "wrong-key")]); return 0; })); Assert.Equal(before, f.Digest());
    }

    [Fact]
    public void Complete_snapshot_rejects_orphan_sign_in_and_rolls_back_cursor()
    {
        using var f = new AccountIdentityFixture(seed: false); Open(f);
        f.Core.Db.Writer.Write("test.incomplete-sign-in", s => { MirrorApply.SnapshotPage(s, "peer", [Row("sign_in", AccountIdentityFixture.Opaque)]); return 0; });
        var before = f.Digest(); Assert.Throws<ArgumentException>(() => f.Core.Db.Writer.Write("test.orphan-snapshot", s => MirrorApply.EndSnapshot(s, "peer", 0, null, 1001))); Assert.Equal(before, f.Digest());
    }

    [Fact]
    public void Valid_provider_may_arrive_on_a_later_snapshot_page_without_identity_rewrite()
    {
        using var f = new AccountIdentityFixture(seed: false); Open(f);
        f.Core.Db.Writer.Write("test.sign-in-first-page", s => { MirrorApply.SnapshotPage(s, "peer", [Row("sign_in", AccountIdentityFixture.Opaque)]); return 0; });
        f.Core.Db.Writer.Write("test.provider-later-page", s => { MirrorApply.SnapshotPage(s, "peer", [Row("provider", AccountIdentityFixture.Opaque)]); return 0; });
        var cursor = f.Core.Db.Writer.Write("test.complete-opaque-snapshot", s => MirrorApply.EndSnapshot(s, "peer", 0, new("provider", Target, 3), 1001));
        Assert.Equal("live", cursor.State); Assert.Equal(Target, cursor.At?.EntityId); Assert.Equal(2, f.Core.Count("engine_mirror_row"));
        Assert.Equal(AccountIdentityFixture.Opaque, f.Core.Text("SELECT json_extract(row_json,'$.accountContext') FROM engine_mirror_row WHERE entity_kind='provider'"));
    }

    [Theory]
    [InlineData("event")]
    [InlineData("cursor")]
    public void Events_and_cursor_require_retained_exact_auth_identity_and_rollback_together(string boundary)
    {
        using var f = new AccountIdentityFixture(seed: false); Open(f); f.Core.Db.Writer.Write("test.empty-complete", s => MirrorApply.EndSnapshot(s, "peer", 0, null, 1000)); var before = f.Digest();
        var item = new EngineEvent("peer", 1, "provider.changed", "provider", boundary == "event" ? "missing-provider/" : Target, 3, 1001, false, new(null, null, null, null, "signed_in", 0));
        Assert.Throws<ArgumentException>(() => f.Core.Db.Writer.Write("test.orphan-mirror-update", s => MirrorApply.Events(s, "peer", 0, 1, new CursorAt("provider", boundary == "cursor" ? "missing-provider/" : Target, 3), [item], boundary == "cursor" ? [Row("provider", AccountIdentityFixture.Opaque)] : [], 1001))); Assert.Equal(before, f.Digest());
    }

    [Fact]
    public void Same_batch_provider_row_can_bind_its_event_and_cursor()
    {
        using var f = new AccountIdentityFixture(seed: false); Open(f); f.Core.Db.Writer.Write("test.empty-complete", s => MirrorApply.EndSnapshot(s, "peer", 0, null, 1000));
        var item = new EngineEvent("peer", 1, "provider.changed", "provider", Target, 3, 1001, false, new(null, null, null, null, "signed_in", 0));
        var cursor = f.Core.Db.Writer.Write("test.valid-batched-auth", s => MirrorApply.Events(s, "peer", 0, 1, new("provider", Target, 3), [item], [Row("provider", AccountIdentityFixture.Opaque), Row("sign_in", AccountIdentityFixture.Opaque)], 1001));
        Assert.Equal(1, cursor.Through); Assert.Equal(1, f.Core.Count("engine_event")); Assert.Equal(2, f.Core.Count("engine_mirror_row"));
    }

    private static string Target => AccountIdentityFixture.Provider + "/" + AccountIdentityFixture.Opaque;
    private static void Open(AccountIdentityFixture f) => f.Core.Db.Writer.Write("test.open-mirror", s => { MirrorApply.Open(s, "peer", "Workbox Test", 1000); MirrorApply.BeginSnapshot(s, "peer", 1000); return 0; });
    private static JsonElement Row(string kind, string account, string? id = null) => JsonSerializer.SerializeToElement(new { entityKind = kind, id = id ?? (kind == "provider" ? AccountIdentityFixture.Provider + "/" + account : "operation"), machine = "peer", version = 3, provider = AccountIdentityFixture.Provider, accountContext = account });
}
