using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Tests.Draft.Base;

public sealed partial class MachinesSocketTests
{
    [Fact]
    public async Task Remote_agent_discovery_cannot_reuse_verified_identity_after_disconnect_and_replacement()
    {
        await _box.ProbeAsync(default); await _box.ReadMirrorOwnerAsync(default);
        Assert.Equal("claude/", (await _box.AgentsAsync(default))[0].ProviderTargetId);
        _stub.Drop = true;
        await Assert.ThrowsAsync<MachineUnreachable>(() => _box.AgentsAsync(default));
        _stub.Drop = false; _stub.EngineMachine = "replacement-engine";
        await _box.ProbeAsync(default);
        await Assert.ThrowsAsync<MachineUnreachable>(() => _box.AgentsAsync(default));
        Assert.Equal("replacement-engine", (await _box.ReadMirrorOwnerAsync(default)).Info.Machine);
        Assert.Equal("claude/", (await _box.AgentsAsync(default))[0].ProviderTargetId);
    }
}
