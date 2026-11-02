using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Providers;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth;

public sealed class PreparedProviderCheckTests
{
    [Fact]
    public void Preparation_freezes_full_safe_json_with_bounded_exact_hash_canonical()
    {
        var raw = Facts(modelName: "exact\0" + new string('é', 40_000));
        using var document = JsonDocument.Parse(raw);
        var command = Command(document.RootElement, new("codex/", "probe_provider/codex//1", 1, 1));
        var prepared = EngineCommands.PrepareProviderCheck(command);
        var utf8 = Encoding.UTF8.GetBytes(raw);
        Assert.Equal(utf8, prepared.FactsUtf8);
        Assert.Equal("{\"kind\":\"provider_check_v1\",\"installation\":\"installed\",\"authState\":\"unverified\",\"reason\":null,\"probe\":{\"targetId\":\"codex/\",\"effectId\":\"probe_provider/codex//1\",\"providerVersion\":1,\"claimAttempt\":1},\"factsUtf8Bytes\":" + utf8.LongLength + ",\"factsSha256\":\"" + Convert.ToHexStringLower(SHA256.HashData(utf8)) + "\"}", prepared.CanonicalJournal);
        document.Dispose();
        Assert.Equal(raw, prepared.Command.Facts.GetRawText());
        Assert.True(Encoding.UTF8.GetByteCount(prepared.CanonicalJournal) < 1024);
    }

    [Fact]
    public void Actual_inflight_check_stores_more_than_32k_exact_bytes_and_survives_reopen()
    {
        using var f = Fixture(); var binding = Probe(f);
        var raw = Facts(modelName: "exact\0" + new string('x', 45_000));
        using var json = JsonDocument.Parse(raw);
        var prepared = EngineCommands.PrepareProviderCheck(Command(json.RootElement, binding));
        var result = Apply(f, f.Context("codex/", 1, new(ActorKind.System, "provider.effects")), prepared);
        Assert.True(result.Applied, result.Error);
        Assert.Equal(raw, Stored(f)); Assert.Equal("unverified", f.Text("engine_provider", "codex/", "auth_state"));
        Assert.Equal(1000, f.Core.Number("SELECT checked_ms FROM engine_provider WHERE id='codex/'"));
        f.Core.Reopen(); Assert.Equal(raw, Stored(f));
        Assert.DoesNotContain(f.Core.Events(), e => JsonSerializer.Serialize(e, EngineJson.Default.EngineEvent).Contains("exact", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unverified", "unverified")]
    [InlineData("could_not_check", "unknown")]
    public void Auth_first_positive_survives_explicit_readiness_check_without_losing_full_safe_content(string authState, string factsAuth)
    {
        using var f = Fixture(); Login(f); SettleAutomaticProbeWithoutFacts(f);
        Assert.True(f.Apply(new CheckProvider(), "codex/", 2).Applied);
        Assert.True(f.Apply(new ClaimEffect(), "probe_provider/codex//3", actor: new(ActorKind.System, "engine.worker")).Applied);
        using var json = JsonDocument.Parse(Facts(auth: factsAuth, modelName: "full\0model"));
        var prepared = EngineCommands.PrepareProviderCheck(Command(json.RootElement, new("codex/", "probe_provider/codex//3", 3, 1), authState));
        var result = Apply(f, f.Context("codex/", 3, new(ActorKind.System, "provider.effects")), prepared);
        Assert.True(result.Applied, result.Error);
        Assert.Equal("signed_in", f.Text("engine_provider", "codex/", "auth_state"));
        using var stored = JsonDocument.Parse(Stored(f));
        Assert.Equal("signedIn", stored.RootElement.GetProperty("auth").GetString());
        Assert.Equal("full\0model", stored.RootElement.GetProperty("models")[0].GetProperty("name").GetString());
        Assert.Equal("Login", stored.RootElement.GetProperty("methods")[0].GetProperty("name").GetString());
        Assert.Equal(1, f.Core.Count("engine_sign_in"));
    }

    [Fact]
    public void Real_negative_check_overrides_retained_positive_and_old_callback_cannot_restore_it()
    {
        using var f = Fixture(); Login(f); SettleAutomaticProbeWithoutFacts(f);
        Assert.True(f.Apply(new CheckProvider(), "codex/", 2).Applied);
        Assert.True(f.Apply(new ClaimEffect(), "probe_provider/codex//3", actor: new(ActorKind.System, "engine.worker")).Applied);
        using var json = JsonDocument.Parse(Facts(auth: "needsSignIn"));
        var prepared = EngineCommands.PrepareProviderCheck(Command(json.RootElement, new("codex/", "probe_provider/codex//3", 3, 1), "needs_sign_in"));
        Assert.True(Apply(f, f.Context("codex/", 3, new(ActorKind.System, "provider.effects")), prepared).Applied);
        using var unknown = JsonDocument.Parse(Facts(auth: "unknown"));
        var old = EngineCommands.PrepareProviderCheck(Command(unknown.RootElement, new("codex/", "probe_provider/codex//3", 3, 1), "could_not_check"));
        Assert.False(Apply(f, f.Context("codex/", 3, new(ActorKind.System, "provider.effects")), old).Applied);
        Assert.Equal("needs_sign_in", f.Text("engine_provider", "codex/", "auth_state"));
        using var stored = JsonDocument.Parse(Stored(f)); Assert.Equal("needsSignIn", stored.RootElement.GetProperty("auth").GetString());
    }

    [Fact]
    public void Exact_prepared_receipt_replays_before_removed_configuration_and_live_provider_version()
    {
        var configured = true; using var f = new AuthRecordFixture((p, a) => configured && p == "codex" && a == "");
        var binding = Probe(f); using var json = JsonDocument.Parse(Facts());
        var prepared = EngineCommands.PrepareProviderCheck(Command(json.RootElement, binding));
        var context = f.Context("codex/", 1, new(ActorKind.System, "provider.effects"));
        var first = Apply(f, context, prepared); Assert.True(first.Applied, first.Error);
        var bytes = Stored(f); configured = false;
        Assert.Equal(first, Apply(f, context with { NowMs = 999999 }, prepared));
        Assert.Equal(bytes, Stored(f)); Assert.Equal(2, f.Version("engine_provider", "codex/"));
    }

    [Theory]
    [InlineData(ActorKind.Window, "owner")]
    [InlineData(ActorKind.System, "adapter")]
    public void Prepared_facts_cannot_borrow_another_actor(ActorKind kind, string actor)
    {
        using var f = Fixture(); var binding = Probe(f); using var json = JsonDocument.Parse(Facts());
        var prepared = EngineCommands.PrepareProviderCheck(Command(json.RootElement, binding));
        var result = Apply(f, f.Context("codex/", 1, new(kind, actor)), prepared);
        Assert.False(result.Applied); Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
        Assert.Equal(1, f.Version("engine_provider", "codex/"));
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("effect")]
    [InlineData("target")]
    [InlineData("version")]
    [InlineData("order")]
    public void Wrong_original_probe_binding_never_records_safe_facts(string fault)
    {
        using var f = Fixture(); var binding = Probe(f); using var json = JsonDocument.Parse(Facts());
        if (fault == "order") f.Core.Db.Writer.Write("test.auth.bad-key", s => s.Execute("UPDATE engine_outbox SET order_key='provider/other' WHERE id='probe_provider/codex//1'"));
        binding = fault switch
        {
            "claim" => binding with { ClaimAttempt = 2 },
            "effect" => binding with { EffectId = "copied" },
            "target" => binding with { TargetId = "codex/other" },
            "version" => binding with { ProviderVersion = 2 },
            _ => binding
        };
        var prepared = EngineCommands.PrepareProviderCheck(Command(json.RootElement, binding));
        Assert.False(Apply(f, f.Context("codex/", 1, new(ActorKind.System, "provider.effects")), prepared).Applied);
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json")); Assert.Equal(1, f.Version("engine_provider", "codex/"));
    }

    [Theory]
    [InlineData("args")]
    [InlineData("env")]
    [InlineData("challenge")]
    [InlineData("duplicate")]
    public void Preparation_rejects_non_safe_shapes_instead_of_journaling_secret_fields(string field)
    {
        var raw = Facts();
        raw = field switch
        {
            "args" => raw.Replace("\"kind\":\"agent\"", "\"kind\":\"agent\",\"args\":[\"SECRET-CANARY\"]", StringComparison.Ordinal),
            "env" => raw.Replace("\"kind\":\"agent\"", "\"kind\":\"agent\",\"env\":{\"TOKEN\":\"SECRET-CANARY\"}", StringComparison.Ordinal),
            "duplicate" => raw.Replace("\"auth\":\"unverified\"", "\"auth\":\"signedIn\",\"auth\":\"unverified\"", StringComparison.Ordinal),
            _ => raw[..^1] + ",\"challenge\":\"https://secret/?code=SECRET-CANARY\"}"
        };
        using var json = JsonDocument.Parse(raw);
        Assert.Throws<ArgumentException>(() => EngineCommands.PrepareProviderCheck(Command(json.RootElement, new("codex/", "probe_provider/codex//1", 1, 1))));
    }

    [Fact]
    public void Callback_storage_and_receipt_fault_roll_back_together_then_same_prepared_input_retries()
    {
        using var f = Fixture(); var binding = Probe(f); using var json = JsonDocument.Parse(Facts());
        var prepared = EngineCommands.PrepareProviderCheck(Command(json.RootElement, binding));
        var context = f.Context("codex/", 1, new(ActorKind.System, "provider.effects"));
        f.Core.Db.Writer.Write("test.auth.store-fault", s => s.Execute("CREATE TRIGGER reject_facts BEFORE UPDATE OF facts_json ON engine_provider BEGIN SELECT RAISE(ABORT,'facts fault'); END"));
        var receipts = f.Core.Count("engine_command_receipt"); var events = f.Core.Count("engine_event");
        Assert.Throws<SqliteException>(() => Apply(f, context, prepared));
        Assert.Equal(receipts, f.Core.Count("engine_command_receipt")); Assert.Equal(events, f.Core.Count("engine_event"));
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json")); Assert.Equal(1, f.Version("engine_provider", "codex/"));
        f.Core.Db.Writer.Write("test.auth.retry-store", s => s.Execute("DROP TRIGGER reject_facts"));
        Assert.True(Apply(f, context, prepared).Applied); Assert.Equal(Facts(), Stored(f));
    }

    [Fact]
    public void Changed_safe_body_under_same_command_id_conflicts_without_replacing_committed_facts()
    {
        using var f = Fixture(); var binding = Probe(f); using var first = JsonDocument.Parse(Facts());
        var context = f.Context("codex/", 1, new(ActorKind.System, "provider.effects"));
        Assert.True(Apply(f, context, EngineCommands.PrepareProviderCheck(Command(first.RootElement, binding))).Applied);
        using var changed = JsonDocument.Parse(Facts(modelName: "different"));
        var result = Apply(f, context, EngineCommands.PrepareProviderCheck(Command(changed.RootElement, binding)));
        Assert.False(result.Applied); Assert.Equal("command_id_conflict", result.Error); Assert.Equal(Facts(), Stored(f));
    }

    private static AuthRecordFixture Fixture() => new((p, a) => p == "codex" && a == "");
    private static ProviderProbeBinding Probe(AuthRecordFixture f)
    {
        Assert.True(f.Apply(new CheckProvider(), "codex/").Applied);
        Assert.True(f.Apply(new ClaimEffect(), "probe_provider/codex//1", actor: new(ActorKind.System, "engine.worker")).Applied);
        return new("codex/", "probe_provider/codex//1", 1, 1);
    }
    private static RecordProviderCheck Command(JsonElement facts, ProviderProbeBinding binding, string auth = "unverified") => new("installed", auth, facts, null, binding);
    private static CommandResult Apply(AuthRecordFixture f, CommandContext context, PreparedProviderCheck prepared) =>
        f.Core.Db.Writer.Write("test.auth.prepared-check", scope => f.Commands.ApplyProviderCheck(scope, context, prepared));
    private static string Stored(AuthRecordFixture f) => f.Core.Db.QueryFirst("SELECT CAST(facts_json AS BLOB) FROM engine_provider WHERE id='codex/'", r => Encoding.UTF8.GetString(r.Blob(0)), default)!;
    private static string Facts(string auth = "unverified", string modelName = "Model") =>
        "{ \"provider\":\"codex\",\"name\":\"Codex\",\"adapter\":\"Adapter\",\"version\":null,\"installation\":\"installed\",\"auth\":" + JsonSerializer.Serialize(auth) +
        ",\"problem\":null,\"models\":[{\"id\":\"model\",\"name\":" + JsonSerializer.Serialize(modelName) + "}],\"methods\":[{\"methodId\":\"login\",\"name\":\"Login\",\"kind\":\"agent\"}],\"checkedMs\":123 }";
    private static void Login(AuthRecordFixture f)
    {
        Assert.True(f.Apply(new StartSignIn("codex", "", null, null), "operation").Applied);
        Assert.True(f.Apply(new ClaimEffect(), "begin_sign_in/operation", actor: new(ActorKind.System, "engine.worker")).Applied);
        Assert.True(f.Apply(new CompleteEffect(new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new SignInEffectData("operation", "started"), EngineJson.Default.SignInEffectData), null)), "begin_sign_in/operation", 1, new(ActorKind.System, "engine.worker")).Applied);
        var binding = new SignInAttemptBinding("operation", "codex/", "begin_sign_in/operation", 1);
        Assert.True(f.Apply(new SignInProgress("finishing", null, binding), "operation", 1, new(ActorKind.System, "sign_in.effects")).Applied);
        Assert.True(f.Apply(new SignInProgress("signed_in", null, binding), "operation", 2, new(ActorKind.System, "sign_in.effects")).Applied);
    }
    private static void SettleAutomaticProbeWithoutFacts(AuthRecordFixture f)
    {
        Assert.Null(f.Text("engine_provider", "codex/", "facts_json"));
        Assert.True(f.Apply(new ClaimEffect(), "probe_provider/codex//2", actor: new(ActorKind.System, "engine.worker")).Applied);
        Assert.True(f.Apply(new CompleteEffect(new(EffectDisposition.Failed, JsonSerializer.SerializeToElement((string?)null), "interrupted")), "probe_provider/codex//2", 1, new(ActorKind.System, "engine.worker")).Applied);
    }
}
