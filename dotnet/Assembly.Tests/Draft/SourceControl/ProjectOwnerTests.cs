using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class ProjectOwnerTests
{
    [Fact]
    public async Task A_committed_profile_change_refuses_the_old_active_owner_before_replacement_runs()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var beforeConnections = hosts.Mac.Topics.Subscribe("saved-machines", null, _ => { }, (_, json) =>
        {
            if (!json.Contains("changed-profile", StringComparison.Ordinal)) return;
            committed.TrySetResult(); release.Task.GetAwaiter().GetResult();
        });
        await hosts.Start(); await hosts.StartMirror();
        var connections = hosts.Connections;
        var owner = await connections.CaptureProjectOwnerAsync("workbox-test", default);
        var profiles = new MachineStore(hosts.Mac.Fixture.Db);
        var update = Task.Run(() => profiles.Import(owner.Profile with { Ssh = "changed-profile" }));
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Same(owner.Machine, connections.StartupSnapshot("workbox-test")!.Value.Machine);
            Assert.Throws<CallFailed>(() => connections.RequireProjectOwner(owner, default));
            await Assert.ThrowsAsync<CallFailed>(() => connections.CaptureProjectOwnerAsync("workbox-test", default));
        }
        finally { release.TrySetResult(); }
        await update.WaitAsync(TimeSpan.FromSeconds(15));
        await connections.ReadyAsync("workbox-test").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotSame(owner.Machine, connections.StartupSnapshot("workbox-test")!.Value.Machine);
        Assert.Throws<CallFailed>(() => connections.RequireProjectOwner(owner, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnect_or_same_instance_socket_replacement_invalidates_the_captured_owner(bool disconnect)
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        await hosts.Start(); await hosts.StartMirror();
        var owner = await hosts.Connections.CaptureProjectOwnerAsync("workbox-test", default);
        if (disconnect) await hosts.DisconnectMirror();
        else
        {
            hosts.Remote.Endpoint.DropAll(); hosts.Remote.Commit(1);
            await hosts.Reached(hosts.Remote.Through);
            Assert.Same(owner.Machine, hosts.ConnectedMachine);
        }
        Assert.Throws<CallFailed>(() => hosts.Connections.RequireProjectOwner(owner, default));
        if (disconnect)
            await Assert.ThrowsAsync<CallFailed>(() => hosts.Connections.CaptureProjectOwnerAsync("workbox-test", default));
        else
        {
            var current = await hosts.Connections.CaptureProjectOwnerAsync("workbox-test", default);
            Assert.NotSame(owner.Socket, current.Socket);
            Assert.NotSame(owner.Binding, current.Binding);
        }
    }
}
