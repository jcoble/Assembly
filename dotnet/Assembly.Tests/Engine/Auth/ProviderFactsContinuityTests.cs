using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Providers;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth;

public sealed class ProviderFactsContinuityTests
{
    [Fact]
    public void First_failure_without_catalogue_leaves_facts_sql_null()
    {
        using var f = Fixture(); var probe = BeginCheck(f, 0);
        Assert.True(Record(f, probe, "could_not_check", "null", "network").Applied);
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
        Assert.Equal("could_not_check", f.Text("engine_provider", "codex/", "auth_state"));
    }

    [Fact]
    public void Failed_check_without_catalogue_preserves_full_existing_positive_body_exactly()
    {
        using var f = Fixture(); EstablishPositive(f); var before = Body(f); var probe = BeginCheck(f, 2);
        Assert.True(Record(f, probe, "could_not_check", "null", "network").Applied);
        Assert.Equal(before, Body(f)); Assert.Equal("signed_in", f.Text("engine_provider", "codex/", "auth_state"));
    }

    [Fact]
    public void Negative_without_new_catalogue_changes_only_prior_auth_and_later_unknown_cannot_resurrect_it()
    {
        using var f = Fixture(); EstablishPositive(f); var probe = BeginCheck(f, 2);
        Assert.True(Record(f, probe, "needs_sign_in", "null").Applied);
        Assert.Equal("needs_sign_in", f.Text("engine_provider", "codex/", "auth_state"));
        using (var stored = JsonDocument.Parse(Body(f)))
        {
            Assert.Equal("needsSignIn", stored.RootElement.GetProperty("auth").GetString());
            Assert.Equal("full\0model", stored.RootElement.GetProperty("models")[0].GetProperty("name").GetString());
            Assert.Equal("Adapter", stored.RootElement.GetProperty("adapter").GetString());
        }
        Assert.True(f.Apply(new CompleteEffect(new(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new ProviderEffectData("codex/", 3), EngineJson.Default.ProviderEffectData), null)),
            probe.EffectId, 1, new(ActorKind.System, "engine.worker")).Applied);
        var next = BeginCheck(f, 4);
        Assert.True(Record(f, next, "could_not_check", "null", "network").Applied);
        Assert.Equal("could_not_check", f.Text("engine_provider", "codex/", "auth_state"));
        using var after = JsonDocument.Parse(Body(f)); Assert.Equal("needsSignIn", after.RootElement.GetProperty("auth").GetString());
    }

    [Fact]
    public void Negative_without_any_catalogue_preserves_sql_null()
    {
        using var f = Fixture(); var probe = BeginCheck(f, 0);
        Assert.True(Record(f, probe, "needs_sign_in", "null").Applied);
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
        Assert.Equal("needs_sign_in", f.Text("engine_provider", "codex/", "auth_state"));
    }

    [Fact]
    public void Negative_projection_and_command_receipt_roll_back_together_then_exact_retry_succeeds()
    {
        using var f = Fixture(); EstablishPositive(f); var before = Body(f); var probe = BeginCheck(f, 2);
        using var facts = JsonDocument.Parse("null");
        var prepared = EngineCommands.PrepareProviderCheck(new RecordProviderCheck("installed", "needs_sign_in", facts.RootElement, null, probe));
        var context = f.Context("codex/", 3, new(ActorKind.System, "provider.effects"));
        f.Core.Db.Writer.Write("test.auth.negative-fault", s => s.Execute("CREATE TRIGGER reject_negative BEFORE INSERT ON engine_command_receipt WHEN EXISTS(SELECT 1 FROM engine_provider WHERE auth_state='needs_sign_in') BEGIN SELECT RAISE(ABORT,'negative fault'); END"));
        var count = f.Core.Count("engine_command_receipt");
        Assert.Throws<SqliteException>(() => f.Core.Db.Writer.Write("test.auth.negative", s => f.Commands.ApplyProviderCheck(s, context, prepared)));
        Assert.Equal(count, f.Core.Count("engine_command_receipt")); Assert.Equal(before, Body(f)); Assert.Equal(3, f.Version("engine_provider", "codex/"));
        f.Core.Db.Writer.Write("test.auth.remove-fault", s => s.Execute("DROP TRIGGER reject_negative"));
        Assert.True(f.Core.Db.Writer.Write("test.auth.negative", s => f.Commands.ApplyProviderCheck(s, context, prepared)).Applied);
        using var stored = JsonDocument.Parse(Body(f)); Assert.Equal("needsSignIn", stored.RootElement.GetProperty("auth").GetString());
    }

    private static AuthRecordFixture Fixture() => new((p, a) => p == "codex" && a == "");
    private static string Body(AuthRecordFixture f) => f.Text("engine_provider", "codex/", "facts_json")!;
    private static ProviderProbeBinding BeginCheck(AuthRecordFixture f, long version)
    {
        Assert.True(f.Apply(new CheckProvider(), "codex/", version).Applied);
        var id = $"probe_provider/codex//{version + 1}";
        Assert.True(f.Apply(new ClaimEffect(), id, actor: new(ActorKind.System, "engine.worker")).Applied);
        return new("codex/", id, version + 1, 1);
    }
    private static CommandResult Record(AuthRecordFixture f, ProviderProbeBinding probe, string auth, string raw, string? reason = null)
    {
        using var facts = JsonDocument.Parse(raw);
        var prepared = EngineCommands.PrepareProviderCheck(new RecordProviderCheck("installed", auth, facts.RootElement, reason, probe));
        return f.Core.Db.Writer.Write("test.auth.record-no-catalogue", s => f.Commands.ApplyProviderCheck(s,
            f.Context("codex/", probe.ProviderVersion, new(ActorKind.System, "provider.effects")), prepared));
    }
    private static void EstablishPositive(AuthRecordFixture f)
    {
        var probe = BeginCheck(f, 0);
        var raw = JsonSerializer.Serialize(new
        {
            provider = "codex",
            name = "Codex",
            adapter = "Adapter",
            version = (string?)null,
            installation = "installed",
            auth = "signedIn",
            problem = (string?)null,
            models = new[] { new { id = "model", name = "full\0model" } },
            methods = new[] { new { methodId = "login", name = "Login", kind = "agent" } },
            checkedMs = 123
        });
        Assert.True(Record(f, probe, "signed_in", raw).Applied);
        Assert.True(f.Apply(new CompleteEffect(new(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new ProviderEffectData("codex/", 1), EngineJson.Default.ProviderEffectData), null)),
            probe.EffectId, 1, new(ActorKind.System, "engine.worker")).Applied);
    }
}
