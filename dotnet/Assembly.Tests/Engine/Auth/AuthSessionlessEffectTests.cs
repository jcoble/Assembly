using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Providers;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthSessionlessEffectTests
{
    [Theory]
    [InlineData("probe_provider")]
    [InlineData("begin_sign_in")]
    [InlineData("cancel_sign_in")]
    public void Exact_auth_receipt_confirms_without_session_dereference_or_session_event(string kind)
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == "");
        var (id, receipt) = Seed(f, kind);
        Assert.True(f.Apply(new ClaimEffect(), id, actor: new(ActorKind.System, "engine.worker")).Applied);
        var accepted = f.Apply(new CompleteEffect(receipt), id, 1, new(ActorKind.System, "engine.worker"));
        Assert.True(accepted.Applied, accepted.Error);
        Assert.Equal("done", f.Text("engine_outbox", id, "state"));
        Assert.Equal(0, f.Core.Count("engine_session"));
        Assert.DoesNotContain(f.Core.Events(), e => e.EntityKind is "session" or "run");
        Assert.Equal("needs_sign_in", f.Text("engine_provider", "codex/", "auth_state"));
    }

    [Theory]
    [InlineData("probe_provider")]
    [InlineData("begin_sign_in")]
    [InlineData("cancel_sign_in")]
    public void Wrong_target_version_or_started_stopped_receipt_refuses_without_finalization(string kind)
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == "");
        var (id, receipt) = Seed(f, kind);
        Assert.True(f.Apply(new ClaimEffect(), id, actor: new(ActorKind.System, "engine.worker")).Applied);
        var wrong = kind == "probe_provider"
            ? JsonSerializer.SerializeToElement(new ProviderEffectData("codex/", 2), EngineJson.Default.ProviderEffectData)
            : JsonSerializer.SerializeToElement(new SignInEffectData("operation", kind == "begin_sign_in" ? "stopped" : "started"), EngineJson.Default.SignInEffectData);
        var result = f.Apply(new CompleteEffect(receipt with { Data = wrong }), id, 1, new(ActorKind.System, "engine.worker"));
        Assert.False(result.Applied); Assert.Equal("bad_receipt", result.Error);
        Assert.Equal("inflight", f.Text("engine_outbox", id, "state"));
        Assert.Null(f.Text("engine_outbox", id, "receipt_json"));
    }

    [Theory]
    [InlineData(ActorKind.Window, "owner")]
    [InlineData(ActorKind.System, "provider.effects")]
    public void Raw_provider_check_facts_require_the_closed_prepared_path(ActorKind actor, string actorId)
    {
        using var f = new AuthRecordFixture((_, _) => true);
        var command = new RecordProviderCheck("installed", "signed_in", JsonSerializer.SerializeToElement(new { auth = "signedIn" }), null,
            new("codex/", "probe_provider/codex//1", 1, 1));
        var result = f.Apply(command, "codex/", 1, new(actor, actorId));
        Assert.False(result.Applied); Assert.Equal("forbidden", result.Error);
        Assert.Equal(0, f.Core.Count("engine_provider"));
    }

    private static (string Id, EffectReceipt Receipt) Seed(AuthRecordFixture f, string kind)
    {
        f.SeedProvider();
        var id = kind == "probe_provider" ? "probe_provider/codex//1" : kind + "/operation";
        if (kind != "probe_provider")
            f.Core.Db.Writer.Write("test.auth.operation", s => s.Execute("""
                INSERT INTO engine_sign_in(id,version,machine,created_ms,updated_ms,provider,account_context,method_id,state,deadline_ms)
                VALUES('operation',1,$machine,1000,1000,'codex','','method',$state,301000)
                """, new("$machine", f.Core.Machine), new("$state", kind == "cancel_sign_in" ? "cancelled" : "starting")));
        var payload = kind switch
        {
            "probe_provider" => JsonSerializer.SerializeToElement(new ProbeProviderEffect("codex/", 1), EngineJson.Default.ProbeProviderEffect),
            "begin_sign_in" => JsonSerializer.SerializeToElement(new BeginSignInEffect("operation", "codex", "method"), EngineJson.Default.BeginSignInEffect),
            _ => JsonSerializer.SerializeToElement(new CancelSignInEffect("operation"), EngineJson.Default.CancelSignInEffect)
        };
        f.Core.Db.Writer.Write("test.auth.effect", s => EngineJournal.Commit(s, f.Context(id), 0, 0, [],
            [new(id, kind, null, null, payload, "provider/codex/")]));
        var data = kind == "probe_provider"
            ? JsonSerializer.SerializeToElement(new ProviderEffectData("codex/", 1), EngineJson.Default.ProviderEffectData)
            : JsonSerializer.SerializeToElement(new SignInEffectData("operation", kind == "begin_sign_in" ? "started" : "stopped"), EngineJson.Default.SignInEffectData);
        return (id, new(EffectDisposition.Confirmed, data, null));
    }
}
