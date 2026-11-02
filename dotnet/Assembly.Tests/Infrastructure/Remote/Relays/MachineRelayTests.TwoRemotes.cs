using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Recording;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Relays;

namespace Assembly.Tests.Infrastructure.Remote.Relays;

public sealed partial class MachineRelayTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(4194321)]
    public async Task Two_actual_configured_remote_owners_transfer_full_bytes_through_mac(int size)
    {
        var delivery = new Delivery();
        await using var target = new TwoHosts.Host(effects: delivery, machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await using var h = new TwoHosts(forwardCommands: true, machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await target.Start(); await h.Start();
        var source = h.Remote;
        var sender = Guid.NewGuid().ToString(); var recipient = Guid.NewGuid().ToString();
        var unrelatedSource = Guid.NewGuid().ToString(); var unrelatedTarget = Guid.NewGuid().ToString();
        source.Fixture.SeedSession(sender); target.Fixture.SeedSession(recipient);
        source.Fixture.SeedSession(unrelatedSource); target.Fixture.SeedSession(unrelatedTarget);
        Assert.True(source.Fixture.Apply(new RegisterForeignSession(target.Runtime.Machine, "codex", "Recipient"), recipient,
            actor: new(ActorKind.System, "machine:" + target.Runtime.Machine)).Applied);
        var title = new string('L', size) + "\0ACTUAL_SOURCE_TITLE😀";
        Assert.True(source.Fixture.Db.Writer.Write("test.title", scope => source.Runtime.Commands.ApplyPreparedSessionTitle(scope,
            new(Guid.NewGuid(), source.Runtime.Machine, new(ActorKind.System, "observer"), sender, 0, 0, 1000),
            EngineCommands.PrepareSessionTitle(new ReportSessionTitle(title)))).Applied);
        Assert.True(target.Fixture.Db.Writer.Write("test.sender", scope => target.Runtime.Commands.ApplyPreparedSessionTitle(scope,
            new(Guid.NewGuid(), target.Runtime.Machine, new(ActorKind.System, "machine:" + source.Runtime.Machine), sender, 0, 0, 1000),
            EngineCommands.PrepareSessionTitle(new RegisterForeignSession(source.Runtime.Machine, "codex", title)))).Applied);
        var body = new string('x', size) + "\0TWO_REMOTE_BODY😀";
        var prepared = PreparedHelperPrompt.Prepare(new(sender, recipient, body, "prompt"), title, 0);
        Assert.True(source.Fixture.Db.Writer.Write("test.source", scope => source.Runtime.Commands.ApplyPreparedHelperPrompt(scope,
            new(Guid.NewGuid(), source.Runtime.Machine, new(ActorKind.Session, sender), "message", 0, 0, 1000), prepared)).Applied);
        await h.StartMirror(); await h.AddMirror("workbox-test-two", target);
        Assert.Equal(2, h.Connections.CaptureMirrorOwners().Count);
        var progress = new int[2];
        async Task Advance(TwoHosts.Host host, string session, int index)
        {
            Assert.True(host.Fixture.Apply(new TurnUsage(new(session, null, null, null, 0), Guid.NewGuid().ToString(), 1, 1, 0, 1, 1),
                Guid.NewGuid().ToString(), actor: new(ActorKind.System, "adapter")).Applied);
            await h.Reached(host, host.Through); Interlocked.Increment(ref progress[index]);
        }
        source.MachineReading = (method, _) => method == "relay_source_part" ? Advance(source, unrelatedSource, 0) : Task.CompletedTask;
        target.MachineWriting = (method, _) => method == "relay_send_part" ? Advance(target, unrelatedTarget, 1) : Task.CompletedTask;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = source.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) =>
        {
            if (source.Fixture.State("engine_message", "message") == "next_turn") settled.TrySetResult();
        });
        await using var relay = new MachineRelay(h.Mac.Runtime, h.Mac.Forwarder!, h.Connections, h.Mac.Topics, h.Mac.Fixture.Time, h.MirrorLog);
        try
        {
            relay.Start();
            try { await settled.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException error) { throw new InvalidOperationException(h.MirrorLog.ToString(), error); }
            var sent = Assert.Single(delivery.Sent);
            Assert.Equal(MessageText.Prompt(MessageText.Label(title, sender, "session"), body), sent.Text);
            Assert.True(progress.All(count => count >= (size > 4194304 ? 10 : 2)));
            Assert.True(Encoding.UTF8.GetBytes(body).AsSpan().SequenceEqual(target.Fixture.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id='message'", row => row.Blob(0), default)));
            Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
            Assert.Equal(1, target.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%'"));
            Assert.Equal(1, source.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-outcome/v1/%'"));
        }
        finally { source.Topics.Unsubscribe(sub); }
    }
}
