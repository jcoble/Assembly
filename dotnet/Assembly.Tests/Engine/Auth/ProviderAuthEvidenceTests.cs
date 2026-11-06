using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Providers;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth;

public sealed class ProviderAuthEvidenceTests
{
    [Fact]
    public void Auth_first_login_then_explicit_check_retains_positive_on_the_same_probe_without_fake_facts()
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == "");
        LoginWithoutFacts(f);
        Assert.Equal("signed_in", f.Text("engine_provider", "codex/", "auth_state"));
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
        Assert.True(f.Apply(new CheckProvider(), "codex/", 2).Applied);
        Assert.Equal("checking", f.Text("engine_provider", "codex/", "auth_state"));
        var original = f.Effect("probe_provider/codex//3").Payload.GetRawText();
        Assert.Equal("signed_in", f.Effect("probe_provider/codex//3").Payload.GetProperty("priorAuthState").GetString());
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
        Assert.True(f.Apply(new CheckProvider(), "codex/", 3).Applied);
        Assert.Equal(3, f.Version("engine_provider", "codex/"));
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='probe_provider'"));
        Assert.Equal(original, f.Effect("probe_provider/codex//3").Payload.GetRawText());
        f.Core.Reopen();
        Assert.Equal(original, f.Effect("probe_provider/codex//3").Payload.GetRawText());
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
    }

    [Theory]
    [InlineData("unverified")]
    [InlineData("needs_sign_in")]
    public void Explicit_check_never_manufactures_prior_positive_from_other_auth_states(string state)
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == ""); f.SeedProvider(state: state);
        Assert.True(f.Apply(new CheckProvider(), "codex/", 1).Applied);
        Assert.False(f.Effect("probe_provider/codex//2").Payload.TryGetProperty("priorAuthState", out _));
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("inflight")]
    [InlineData("uncertain")]
    [InlineData("unavailable")]
    public void Initialize_interrupt_reads_actual_probe_state_and_restores_retained_auth_without_launching(string state)
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == "");
        SeedCheckingProbe(f, "signed_in", state);
        var payload = f.Effect("probe_provider/codex//1").Payload.GetRawText();
        var effects = f.Core.Count("engine_outbox");
        var result = f.Apply(new InterruptProviderCheck(), "codex/", 1, new(ActorKind.System, "engine.initialize"));
        Assert.True(result.Applied, result.Error);
        Assert.Equal("signed_in", f.Text("engine_provider", "codex/", "auth_state"));
        Assert.Equal("interrupted", f.Text("engine_provider", "codex/", "reason"));
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
        Assert.Equal(effects, f.Core.Count("engine_outbox"));
        Assert.Equal(payload, f.Effect("probe_provider/codex//1").Payload.GetRawText());
        Assert.Equal(state, f.Text("engine_outbox", "probe_provider/codex//1", "state"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("signedIn")]
    [InlineData("forged")]
    public void Missing_or_malformed_retained_value_cannot_grant_positive_authority(string? prior)
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == ""); SeedCheckingProbe(f, prior, "pending");
        var result = f.Apply(new InterruptProviderCheck(), "codex/", 1, new(ActorKind.System, "engine.initialize"));
        Assert.True(result.Applied, result.Error);
        Assert.Equal("could_not_check", f.Text("engine_provider", "codex/", "auth_state"));
        Assert.Equal("interrupted", f.Text("engine_provider", "codex/", "reason"));
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
    }

    [Fact]
    public void Check_capture_effect_and_provider_rollback_together_and_the_same_request_retries()
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == ""); LoginWithoutFacts(f);
        f.Core.Db.Writer.Write("test.auth.capture-fault", s => s.Execute("CREATE TRIGGER reject_probe BEFORE INSERT ON engine_outbox WHEN NEW.kind='probe_provider' BEGIN SELECT RAISE(ABORT,'capture fault'); END"));
        var context = f.Context("codex/", 2);
        Assert.Throws<SqliteException>(() => f.Apply(context, new CheckProvider()));
        Assert.Equal("signed_in", f.Text("engine_provider", "codex/", "auth_state"));
        Assert.Equal(2, f.Version("engine_provider", "codex/")); Assert.Null(f.Effect("probe_provider/codex//3"));
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
        f.Core.Db.Writer.Write("test.auth.retry-capture", s => s.Execute("DROP TRIGGER reject_probe"));
        Assert.True(f.Apply(context, new CheckProvider()).Applied);
        Assert.Equal("signed_in", f.Effect("probe_provider/codex//3").Payload.GetProperty("priorAuthState").GetString());
    }

    [Fact]
    public void Omitted_retained_field_preserves_exact_original_two_field_probe_bytes() =>
        Assert.Equal("{\"targetId\":\"codex/\",\"providerVersion\":7}",
            JsonSerializer.Serialize(new ProbeProviderEffect("codex/", 7), EngineJson.Default.ProbeProviderEffect));

    private static void LoginWithoutFacts(AuthRecordFixture f)
    {
        Assert.True(f.Apply(new StartSignIn("codex", "", null, null), "operation").Applied);
        Assert.True(f.Apply(new ClaimEffect(), "begin_sign_in/operation", actor: new(ActorKind.System, "engine.worker")).Applied);
        Assert.True(f.Apply(new CompleteEffect(new(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new SignInEffectData("operation", "started"), EngineJson.Default.SignInEffectData), null)),
            "begin_sign_in/operation", 1, new(ActorKind.System, "engine.worker")).Applied);
        var binding = new SignInAttemptBinding("operation", "codex/", "begin_sign_in/operation", 1);
        Assert.True(f.Apply(new SignInProgress("finishing", null, binding), "operation", 1, new(ActorKind.System, "sign_in.effects")).Applied);
        Assert.True(f.Apply(new SignInProgress("signed_in", null, binding), "operation", 2, new(ActorKind.System, "sign_in.effects")).Applied);
    }

    private static void SeedCheckingProbe(AuthRecordFixture f, string? prior, string state)
    {
        f.SeedProvider(state: "checking");
        using var json = JsonDocument.Parse(prior is null ? "{\"targetId\":\"codex/\",\"providerVersion\":1}" :
            "{\"targetId\":\"codex/\",\"providerVersion\":1,\"priorAuthState\":" + JsonSerializer.Serialize(prior) + "}");
        f.Core.Db.Writer.Write("test.auth.retained-probe", s => EngineJournal.Commit(s, f.Context("codex/"), 1, 0, [],
            [new("probe_provider/codex//1", "probe_provider", null, null, json.RootElement.Clone(), "provider/codex/")]));
        if (state == "pending") return;
        Assert.True(f.Apply(new ClaimEffect(), "probe_provider/codex//1", actor: new(ActorKind.System, "engine.worker")).Applied);
        if (state == "inflight") return;
        var disposition = state == "uncertain" ? EffectDisposition.Unknown : EffectDisposition.Unavailable;
        Assert.True(f.Apply(new CompleteEffect(new(disposition, JsonSerializer.SerializeToElement((string?)null), "interrupted")),
            "probe_provider/codex//1", 1, new(ActorKind.System, "engine.worker")).Applied);
    }
}
