using Assembly.Engine;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Handoffs;

namespace Assembly.Tests.Engine;

public sealed class HandoffTargetFenceTests
{
    [Fact]
    public void Ordinary_mapping_without_private_attempt_records_remains_ordinary()
    {
        using var f = new EngineFixture(); f.SeedSession();
        using var scope = f.Db.OpenSnapshot(default);
        var fence = new EngineCommands(f.Machine, f.Limits).ReadTargetHandoffFence(scope, "s");
        Assert.Equal(HandoffFenceState.Ordinary, fence.State);
        Assert.Null(fence.Binding);
    }

    [Fact]
    public void Orphan_closed_marker_is_unknown_and_never_classified_as_ordinary_after_restart()
    {
        using var f = new EngineFixture(); f.SeedSession();
        f.Db.Writer.Write("test.corrupt-marker", scope => scope.Execute(
            "INSERT INTO engine_command_receipt(command_id,fingerprint,result_json) VALUES($key,'corrupt','{}')",
            new SqlArg("$key", HandoffAdmissionKeys.TargetClosed(f.Machine, "s"))));
        f.Reopen();
        using var scope = f.Db.OpenSnapshot(default);
        Assert.Equal(HandoffFenceState.Unknown, new EngineCommands(f.Machine, f.Limits).ReadTargetHandoffFence(scope, "s").State);
    }

    [Fact]
    public void Actual_effect_claim_refuses_orphan_attempt_marker_before_any_provider_work()
    {
        using var f = new EngineFixture();
        var start = new Assembly.Engine.Sessions.StartSession("codex", "model", "/folder", "title", null, null, null);
        Assert.True(f.Apply(start, "s").Applied);
        var effect = f.Text("SELECT id FROM engine_outbox")!;
        f.Db.Writer.Write("test.orphan-claim-fence", scope => scope.Execute(
            "INSERT INTO engine_command_receipt(command_id,fingerprint,result_json) VALUES($key,'corrupt','{}')",
            new SqlArg("$key", HandoffAdmissionKeys.TargetClosed(f.Machine, "s"))));
        Assert.Equal("writer_elsewhere", f.Apply(new Assembly.Engine.Effects.ClaimEffect(), effect,
            actor: new(ActorKind.System, "engine.worker")).Error);
        Assert.Equal("pending", f.State("engine_outbox", effect));
        Assert.Equal(0, f.Number("SELECT attempt FROM engine_outbox WHERE id=$id", new SqlArg("$id", effect)));
    }

    [Fact]
    public void Missing_actual_native_witness_never_allocates_adoption_or_start()
    {
        using var f = new EngineFixture();
        var identity = new HandoffIdentity("H", "source", "S", f.Machine, 7, 8);
        var command = new Assembly.Engine.Sessions.AdoptSession("provider", null, "/target", "title", "actual-native", "source", "S", 8);
        var ack = f.Db.Writer.Write("test.no-native-witness", scope => new EngineCommands(f.Machine, f.Limits)
            .ApplyHandoffAdopt(scope, new(identity, command, new string('a', 64), new string('b', 64)), 1000));
        Assert.Equal(HandoffGate.Waiting, ack.Gate);
        Assert.Equal("native_witness_required", ack.Reason);
        Assert.Null(ack.Receipt);
        Assert.Equal(0, f.Count("engine_session")); Assert.Equal(0, f.Count("engine_command_receipt"));
        Assert.Equal(0, f.Count("engine_outbox")); Assert.Equal(0, f.Count("engine_event"));
    }
}
