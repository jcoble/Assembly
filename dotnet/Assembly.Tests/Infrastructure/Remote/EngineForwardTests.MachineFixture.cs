using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Application.Common.Database;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class EngineForwardTests
{
    [Fact]
    public async Task Machine_fixture_uses_actual_registration_cutover_projection_and_configured_limits()
    {
        var root = Path.Combine(Path.GetTempPath(), "b11-relay-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var launcher = new RelayTestLauncher();
            await using var h = new TwoHosts(windowCalls: true, forwardCommands: true, machineLauncher: launcher, checkouts: root,
                macLimits: new(() => 2, () => 2), remoteLimits: new(() => 3, () => 2));
            await h.Start(); await h.StartMirror();
            var parent = Guid.NewGuid().ToString(); var child = Guid.NewGuid().ToString();
            var source = new StartSession("codex", null, root, "Parent", null, null, null);
            Assert.True(((Assembly.Engine.Hosting.IEngineRuntime)h.Mac.Runtime).Write(s => h.Mac.Runtime.Commands.ApplyPreparedSessionTitle(s,
                new(Guid.NewGuid(), h.Mac.Runtime.Machine, new(ActorKind.Window, "owner"), parent, 0, 0, 1000), EngineCommands.PrepareSessionTitle(source))).Applied);
            var forward = h.Mac.Forwarder!; var remote = h.ConnectedMachine;
            var binding = await remote.CaptureOwnerBindingAsync(h.Remote.Fixture.Machine, default);
            Assert.True((await forward.ForeignAncestryAsync(remote, binding,
                new(h.Mac.Runtime.Machine, parent, [new(parent, h.Mac.Runtime.Machine, "codex", "Parent", null)]), default)).Applied);
            var start = new StartSession("codex", null, root, "Child", parent, null, null, ManagedCheckout: false);
            var answer = await forward.MachineCommandAsync(remote, binding,
                new(Guid.NewGuid(), h.Mac.Runtime.Machine, child, 0, 0, start), default);
            Assert.True(answer.Applied, answer.Error);
            Assert.Equal(parent, h.Remote.Fixture.Text("SELECT parent_session_id FROM session_info WHERE session_id=$id", new SqlArg("$id", child)));
            Assert.Equal(root, h.Remote.Fixture.Text("SELECT repo_url FROM session_info WHERE session_id=$id", new SqlArg("$id", child)));
            Assert.Equal(0, h.Remote.Fixture.Number("SELECT COUNT(*) FROM session_info WHERE session_id=$id", new SqlArg("$id", parent)));
            Assert.Equal(1, h.Mac.Fixture.Number("SELECT COUNT(*) FROM session_info WHERE session_id=$id", new SqlArg("$id", parent)));
            Assert.Equal(2, h.Mac.Fixture.Limits.ReadDepth()); Assert.Equal(3, h.Remote.Fixture.Limits.ReadDepth());
            Assert.Equal(0, launcher.ProviderCalls);
        }
        finally { Directory.Delete(root, true); }
    }
}
