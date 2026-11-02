using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Providers;

namespace Assembly.Tests.Engine.Auth;

public sealed class ProviderRecordTests
{
    [Fact]
    public void Check_commits_one_exact_sessionless_probe_and_nullable_real_facts()
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == "");
        var result = f.Apply(new CheckProvider(), "codex/");
        Assert.True(result.Applied, result.Error); Assert.Equal(1, result.Version);
        Assert.Equal("checking", f.Text("engine_provider", "codex/", "auth_state"));
        Assert.Equal("unknown", f.Text("engine_provider", "codex/", "installation"));
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
        var effect = f.Effect("probe_provider/codex//1");
        Assert.NotNull(effect); Assert.Null(effect.SessionId); Assert.Null(effect.RunId);
        Assert.Equal("provider/codex/", effect.OrderKey);
        Assert.Equal(new ProbeProviderEffect("codex/", 1), effect.Payload.Deserialize(EngineJson.Default.ProbeProviderEffect));
        Assert.Equal(0, f.Core.Count("engine_session"));
        Assert.DoesNotContain(f.Core.Events(), e => e.Data.ToString().Contains("facts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Recheck_while_checking_is_applied_without_second_version_event_or_probe()
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == "");
        Assert.True(f.Apply(new CheckProvider(), "codex/").Applied);
        var before = f.Core.Count("engine_event");
        var repeat = f.Apply(new CheckProvider(), "codex/", 1);
        Assert.True(repeat.Applied, repeat.Error); Assert.Equal(1, repeat.Version);
        Assert.Equal(before, f.Core.Count("engine_event")); Assert.Equal(1, f.Core.Count("engine_outbox"));
    }

    [Theory]
    [InlineData("codex", "/home/test/account/with/slashes")]
    [InlineData("vendor/team/codex", "/home/test/account")]
    [InlineData("vendor/team", "")]
    public void Initial_check_resolves_the_unique_configured_literal_pair(string provider, string account)
    {
        using var f = new AuthRecordFixture((p, a) => p == provider && a == account);
        var id = provider + "/" + account;
        var result = f.Apply(new CheckProvider(), id);
        Assert.True(result.Applied, result.Error);
        Assert.Equal(provider, f.Text("engine_provider", id, "provider"));
        Assert.Equal(account, f.Text("engine_provider", id, "account_context"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_ambiguous_configured_target_creates_no_provider_or_effect(bool ambiguous)
    {
        using var f = new AuthRecordFixture(ambiguous ? (p, a) => (p, a) is ("provider", "team/account") or ("provider/team", "account") : null);
        var result = f.Apply(new CheckProvider(), "provider/team/account");
        Assert.False(result.Applied); Assert.Equal("unavailable", result.Error);
        Assert.Equal(0, f.Core.Count("engine_provider")); Assert.Equal(0, f.Core.Count("engine_outbox"));
    }

    [Fact]
    public void Accepted_command_receipt_replays_before_removed_configuration_or_new_expected_version()
    {
        var configured = true; using var f = new AuthRecordFixture((p, a) => configured && p == "codex" && a == "");
        var context = f.Context("codex/"); var first = f.Apply(context, new CheckProvider());
        Assert.True(first.Applied, first.Error); configured = false;
        Assert.Equal(first, f.Apply(context with { NowMs = 7777 }, new CheckProvider()));
        Assert.Equal(1, f.Core.Count("engine_outbox"));
    }

    [Fact]
    public void Session_peer_cannot_launch_a_provider_probe()
    {
        using var f = new AuthRecordFixture((_, _) => true);
        var result = f.Apply(new CheckProvider(), "codex/", actor: new(ActorKind.Session, "s"));
        Assert.False(result.Applied); Assert.Equal("forbidden", result.Error);
        Assert.Equal(0, f.Core.Count("engine_provider")); Assert.Equal(0, f.Core.Count("engine_outbox"));
    }
}
