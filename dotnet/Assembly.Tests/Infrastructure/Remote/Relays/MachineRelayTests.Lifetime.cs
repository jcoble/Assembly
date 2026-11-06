using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Relays;

namespace Assembly.Tests.Infrastructure.Remote.Relays;

public sealed partial class MachineRelayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_or_coordinator_stop_joins_held_source_read_then_recovers_original_queue(bool disconnectOwner)
    {
        var delivery = new Delivery();
        await using var h = new TwoHosts(forwardCommands: true, effects: delivery, macEffects: delivery,
            machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await h.Start();
        var sender = Guid.NewGuid().ToString(); var recipient = Guid.NewGuid().ToString();
        h.Remote.Fixture.SeedSession(sender); h.Mac.Fixture.SeedSession(recipient);
        Assert.True(h.Remote.Fixture.Apply(new RegisterForeignSession(h.Mac.Runtime.Machine, "codex", "Recipient"), recipient,
            actor: new(ActorKind.System, "machine:" + h.Mac.Runtime.Machine)).Applied);
        Assert.True(h.Mac.Fixture.Apply(new RegisterForeignSession(h.Remote.Runtime.Machine, "codex", "Test"), sender,
            actor: new(ActorKind.System, "machine:" + h.Remote.Runtime.Machine)).Applied);
        var prompt = PreparedHelperPrompt.Prepare(new(sender, recipient, new string('x', 4194321), "prompt"), "Test", 0);
        Assert.True(h.Remote.Fixture.Db.Writer.Write("test.source", scope => h.Remote.Runtime.Commands.ApplyPreparedHelperPrompt(scope,
            new(Guid.NewGuid(), h.Remote.Runtime.Machine, new(ActorKind.Session, sender), "message", 0, 0, 1000), prompt)).Applied);
        await h.StartMirror();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.MachineReading = async (method, call) =>
        {
            if (method != "relay_source_part") return;
            reading.TrySetResult();
            await release.Task.WaitAsync(call.Cancellation);
        };
        var relay = new MachineRelay(h.Mac.Runtime, h.Mac.Forwarder!, h.Connections, h.Mac.Topics, h.Mac.Fixture.Time, h.MirrorLog, h.Mac.Needs);
        try
        {
            relay.Start();
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (disconnectOwner) await h.DisconnectMirror().WaitAsync(TimeSpan.FromSeconds(10));
            else await relay.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(delivery.Sent);
            Assert.Equal("queued", h.Remote.Fixture.State("engine_message", "message"));
            Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
        }
        catch { await relay.DisposeAsync(); throw; }
        finally { release.TrySetResult(); }
        h.Remote.MachineReading = null;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = h.Remote.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) =>
        {
            if (h.Remote.Fixture.State("engine_message", "message") == "next_turn") settled.TrySetResult();
        });
        await using var recovered = disconnectOwner ? relay : new MachineRelay(h.Mac.Runtime, h.Mac.Forwarder!, h.Connections, h.Mac.Topics, h.Mac.Fixture.Time, h.MirrorLog, h.Mac.Needs);
        try
        {
            if (disconnectOwner) await h.ReconnectSaved();
            else recovered.Start();
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Single(delivery.Sent);
            Assert.Equal(1, h.Mac.Fixture.Count("engine_message"));
            Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-outcome/v1/%'"));
        }
        finally { h.Remote.Topics.Unsubscribe(sub); }
    }
}
