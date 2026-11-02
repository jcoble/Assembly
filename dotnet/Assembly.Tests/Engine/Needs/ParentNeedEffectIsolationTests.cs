using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;

namespace Assembly.Tests.Engine.Needs;

public sealed class ParentNeedEffectIsolationTests
{
    [Theory]
    [InlineData("claim", "pending")]
    [InlineData("recover", "inflight")]
    [InlineData("enable", "unavailable")]
    [InlineData("complete", "inflight")]
    public void Generic_effect_commands_cannot_advance_coordinator_intents(string operation, string state)
    {
        using var f = new EngineFixture();
        Seed(f, "intent", "parent_need", state);
        var before = f.Text("SELECT json_array(version,state,attempt,payload_json,next_ms,receipt_json) FROM engine_outbox WHERE id='intent'");
        var command = Command(operation);
        var result = f.Apply(command, "intent", actor: new(ActorKind.System, "engine.worker"));
        Assert.False(result.Applied);
        Assert.Equal("forbidden", result.Error);
        Assert.Equal(before, f.Text("SELECT json_array(version,state,attempt,payload_json,next_ms,receipt_json) FROM engine_outbox WHERE id='intent'"));
        Assert.Equal(0, f.Count("engine_event"));
    }

    [Fact]
    public void Exact_ordinary_receipt_replays_before_new_kind_guard()
    {
        using var f = new EngineFixture();
        Seed(f, "effect", "start", "pending");
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.System, "worker"), "effect", 0, 0, 1000);
        var accepted = f.Apply(context, new ClaimEffect());
        Assert.True(accepted.Applied);
        f.Db.Writer.Write("test.new-kind", s => s.Execute("UPDATE engine_outbox SET kind='parent_need' WHERE id='effect'"));
        Assert.Equal(accepted, f.Apply(context with { NowMs = 9999 }, new ClaimEffect()));
        Assert.Equal(1, f.Count("engine_command_receipt"));
    }

    [Fact]
    public void Coordinator_intent_does_not_reserve_provider_effect_order()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Seed(f, "intent", "parent_need", "uncertain", "s");
        Seed(f, "ordinary", "stop", "pending", "s");
        var accepted = f.Apply(new ClaimEffect(), "ordinary", actor: new(ActorKind.System, "worker"));
        Assert.True(accepted.Applied, accepted.Error);
        Assert.Equal("uncertain", f.State("engine_outbox", "intent"));
        Assert.Equal("inflight", f.State("engine_outbox", "ordinary"));
    }

    private static EffectCommand Command(string operation) => operation switch
    {
        "claim" => new ClaimEffect(),
        "recover" => new RecoverEffect(),
        "enable" => new EnableEffect(),
        _ => new CompleteEffect(new(EffectDisposition.Unknown, JsonSerializer.SerializeToElement((string?)null), "unknown"))
    };

    private static void Seed(EngineFixture f, string id, string kind, string state, string? session = null) =>
        f.Db.Writer.Write("test.effect-isolation", s => s.Execute("""
            INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,run_id,kind,payload_json,state,attempt,next_ms)
            VALUES($id,0,$machine,1000,1000,0,$session,NULL,$kind,'{}',$state,0,NULL)
            """, new("$id", id), new("$machine", f.Machine), new("$session", session), new("$kind", kind), new("$state", state)));
}
