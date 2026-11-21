using Assembly.Engine;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine;

public sealed class HandoffAdmissionTests
{
    [Fact]
    public void No_adopt_seal_is_one_immutable_target_slot_and_late_adopt_does_not_start()
    {
        using var f = new EngineFixture();
        var identity = new HandoffIdentity("H", "source", "S", f.Machine, 7, 8);
        var commands = new EngineCommands(f.Machine, f.Limits);
        var seal = f.Db.Writer.Write("test.seal", scope => commands.SealHandoffTarget(scope, new(identity, "missing"), 1000));
        Assert.Equal(HandoffTargetState.NotAdopted, seal.Target!.State);
        Assert.Equal("handoff_not_adopted", seal.Receipt!.Error);
        var bytes = f.Text("SELECT result_json FROM engine_command_receipt");
        var again = f.Db.Writer.Write("test.seal-repeat", scope => commands.SealHandoffTarget(scope, new(identity, "different diagnostic"), 2000));
        Assert.Equal(seal.Fingerprint, again.Fingerprint);
        Assert.Equal(bytes, f.Text("SELECT result_json FROM engine_command_receipt"));
        Assert.Equal(1, f.Count("engine_command_receipt"));
        var adopt = new Assembly.Engine.Sessions.AdoptSession("provider", null, "/folder", "title", "native", "source", "S", 8);
        var late = f.Db.Writer.Write("test.late-adopt", scope => commands.ApplyHandoffAdopt(scope,
            new(identity, adopt, new string('a', 64), new string('b', 64)), 3000));
        Assert.Equal(HandoffTargetState.NotAdopted, late.Target!.State);
        Assert.Equal(0, f.Count("engine_session")); Assert.Equal(0, f.Count("engine_outbox"));
    }
}
