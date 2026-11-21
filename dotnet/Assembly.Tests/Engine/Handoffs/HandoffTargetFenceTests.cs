using Assembly.Engine;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Handoffs;

namespace Assembly.Tests.Engine;

public sealed class HandoffTargetFenceTests
{
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
}
