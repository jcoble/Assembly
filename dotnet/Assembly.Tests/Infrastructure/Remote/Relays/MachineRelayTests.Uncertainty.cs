using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Relays;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Remote.Relays;

public sealed partial class MachineRelayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bulk_only_lost_commit_reply_reads_actual_private_acceptance_or_refusal_on_same_normal_owner(bool refused)
    {
        var delivery = new Delivery();
        await using var h = new TwoHosts(forwardCommands: true, effects: delivery,
            machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await h.Start();
        var source = h.Mac; var target = h.Remote;
        var sender = Guid.NewGuid().ToString(); var recipient = Guid.NewGuid().ToString();
        source.Fixture.SeedSession(sender); target.Fixture.SeedSession(recipient, state: refused ? "running" : "idle");
        Assert.True(source.Fixture.Apply(new RegisterForeignSession(target.Runtime.Machine, "codex", "Recipient"), recipient,
            actor: new(ActorKind.System, "machine:" + target.Runtime.Machine)).Applied);
        Assert.True(target.Fixture.Apply(new RegisterForeignSession(source.Runtime.Machine, "codex", "Test"), sender,
            actor: new(ActorKind.System, "machine:" + source.Runtime.Machine)).Applied);
        if (refused)
            for (var i = 0; i < 20; i++)
                Assert.True(target.Fixture.Apply(new SendMessage(sender, recipient, "prior-" + i, "prompt"), "before-" + i,
                    actor: new(ActorKind.System, "machine:" + source.Runtime.Machine)).Applied);
        var prompt = PreparedHelperPrompt.Prepare(new(sender, recipient, "exact\0body", "prompt"), "Test", 0);
        Assert.True(source.Fixture.Db.Writer.Write("test.source", scope => source.Runtime.Commands.ApplyPreparedHelperPrompt(scope,
            new(Guid.NewGuid(), source.Runtime.Machine, new(ActorKind.Session, sender), "message", 0, 0, 1000), prompt)).Applied);
        await h.StartMirror();
        var owner = h.Connections.CaptureMirrorOwners().Single();
        var binding = await ReadyBindingAsync(owner.Mirror);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        target.WriteCommitted = what =>
        {
            if (what == "engine/relay_send_commit" && Interlocked.Increment(ref commits) == 1)
            { target.BulkEndpoint.DropAll(); lost.TrySetResult(); }
        };
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = source.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) =>
        {
            if (source.Fixture.State("engine_message", "message") == (refused ? "failed" : "next_turn")) settled.TrySetResult();
        });
        await using var relay = new MachineRelay(h.Mac.Runtime, h.Mac.Forwarder!, h.Connections, h.Mac.Topics, h.Mac.Fixture.Time, h.MirrorLog);
        try
        {
            relay.Start(); await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try { await settled.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException error) { throw new InvalidOperationException(h.MirrorLog.ToString(), error); }
            Assert.True(await owner.Mirror.IsCurrentLiveBindingAsync(binding!, default));
            Assert.Equal(1, commits);
            Assert.Equal(refused ? 0 : 1, delivery.Sent.Count);
            Assert.Equal(1, target.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%'"));
            Assert.Equal(refused ? "message_limit" : null, source.Fixture.Text("SELECT reason FROM engine_message WHERE id='message'"));
            Assert.Equal(refused ? 0 : 1, target.Fixture.Number("SELECT COUNT(*) FROM engine_message WHERE id='message'"));
            Assert.Equal(1, source.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-outcome/v1/%'"));
        }
        finally { source.Topics.Unsubscribe(sub); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_before_original_bulk_writer_commits_never_resends_and_replaced_owner_is_not_probed(bool replaceOwner)
    {
        var delivery = new Delivery();
        await using var h = new TwoHosts(forwardCommands: true, effects: delivery,
            machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await h.Start();
        var sender = Guid.NewGuid().ToString(); var recipient = Guid.NewGuid().ToString();
        h.Mac.Fixture.SeedSession(sender); h.Remote.Fixture.SeedSession(recipient);
        Assert.True(h.Mac.Fixture.Apply(new RegisterForeignSession(h.Remote.Runtime.Machine, "codex", "Recipient"), recipient,
            actor: new(ActorKind.System, "machine:" + h.Remote.Runtime.Machine)).Applied);
        Assert.True(h.Remote.Fixture.Apply(new RegisterForeignSession(h.Mac.Runtime.Machine, "codex", "Test"), sender,
            actor: new(ActorKind.System, "machine:" + h.Mac.Runtime.Machine)).Applied);
        var prompt = PreparedHelperPrompt.Prepare(new(sender, recipient, "exact body", "prompt"), "Test", 0);
        Assert.True(h.Mac.Fixture.Db.Writer.Write("test.source", scope => h.Mac.Runtime.Commands.ApplyPreparedHelperPrompt(scope,
            new(Guid.NewGuid(), h.Mac.Runtime.Machine, new(ActorKind.Session, sender), "message", 0, 0, 1000), prompt)).Applied);
        await h.StartMirror();
        var owner = h.Connections.CaptureMirrorOwners().Single();
        var binding = await ReadyBindingAsync(owner.Mirror);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var begins = 0; var prepares = 0; var afterLoss = 0; var probesAfterLoss = 0;
        h.Remote.MachineWriting = (method, _) => { if (method == "relay_send_begin") Interlocked.Increment(ref begins); return Task.CompletedTask; };
        h.Remote.MachinePrepared = (method, _) =>
        {
            if (method != "relay_send_commit") return Task.CompletedTask;
            Interlocked.Increment(ref prepares); prepared.TrySetResult(); return release.Task;
        };
        h.Remote.MachineReading = (method, _) =>
        {
            if (method == "relay_send_ack" && Volatile.Read(ref afterLoss) != 0)
            { Interlocked.Increment(ref probesAfterLoss); probed.TrySetResult(); }
            return Task.CompletedTask;
        };
        h.Remote.WriteCommitted = what => { if (what == "engine/relay_send_commit") committed.TrySetResult(); };
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = h.Mac.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) =>
        {
            if (h.Mac.Fixture.State("engine_message", "message") == "next_turn") settled.TrySetResult();
        });
        await using var relay = new MachineRelay(h.Mac.Runtime, h.Mac.Forwarder!, h.Connections, h.Mac.Topics, h.Mac.Fixture.Time, h.MirrorLog);
        try
        {
            relay.Start(); await prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Volatile.Write(ref afterLoss, 1);
            if (replaceOwner)
            {
                await h.DisconnectMirror().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, probesAfterLoss);
                Assert.True(binding!.IsInvalidated);
            }
            else
            {
                h.Remote.BulkEndpoint.DropAll();
                await probed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var drained = await owner.Machine.AcquireRelayBulkAsync(binding!.OwnerBinding, wait.Token, binding);
                Assert.True(await owner.Mirror.IsCurrentLiveBindingAsync(binding, default));
            }
            Assert.Equal("queued", h.Mac.Fixture.State("engine_message", "message"));
            Assert.Equal(0, h.Remote.Fixture.Count("engine_message"));
            Assert.Equal(0, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%'"));
            Assert.Equal(1, begins); Assert.Equal(1, prepares); Assert.Empty(delivery.Sent);
            release.TrySetResult(); await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (replaceOwner) await h.ReconnectSaved();
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Single(delivery.Sent); Assert.Equal(1, begins); Assert.Equal(1, prepares);
            Assert.Equal(1, h.Remote.Fixture.Count("engine_message"));
            Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%'"));
        }
        finally { release.TrySetResult(); h.Mac.Topics.Unsubscribe(sub); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_actual_stale_epoch_refusals_use_only_root_and_retry_even_when_first_bulk_reply_is_lost(bool loseFirstReply)
    {
        var delivery = new Delivery();
        await using var h = new TwoHosts(forwardCommands: true, effects: delivery,
            machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await h.Start();
        var sender = Guid.NewGuid().ToString(); var recipient = Guid.NewGuid().ToString();
        h.Mac.Fixture.SeedSession(sender); h.Remote.Fixture.SeedSession(recipient);
        Assert.True(h.Mac.Fixture.Apply(new RegisterForeignSession(h.Remote.Runtime.Machine, "codex", "Recipient"), recipient,
            actor: new(ActorKind.System, "machine:" + h.Remote.Runtime.Machine)).Applied);
        Assert.True(h.Remote.Fixture.Apply(new RegisterForeignSession(h.Mac.Runtime.Machine, "codex", "Test"), sender,
            actor: new(ActorKind.System, "machine:" + h.Mac.Runtime.Machine)).Applied);
        var prompt = PreparedHelperPrompt.Prepare(new(sender, recipient, "exact body", "prompt"), "Test", 0);
        Assert.True(h.Mac.Fixture.Db.Writer.Write("test.source", scope => h.Mac.Runtime.Commands.ApplyPreparedHelperPrompt(scope,
            new(Guid.NewGuid(), h.Mac.Runtime.Machine, new(ActorKind.Session, sender), "message", 0, 0, 1000), prompt)).Applied);
        await h.StartMirror();
        var prepares = 0; var commits = 0;
        h.Remote.MachinePrepared = (method, _) =>
        {
            if (method == "relay_send_commit")
            {
                Interlocked.Increment(ref prepares);
                h.Remote.Fixture.Db.Writer.Write("test.epoch-race", scope => scope.Execute(
                    "UPDATE engine_session SET epoch=epoch+1 WHERE id=$id", new SqlArg("$id", recipient)));
            }
            return Task.CompletedTask;
        };
        h.Remote.WriteCommitted = what =>
        {
            if (what == "engine/relay_send_commit" && Interlocked.Increment(ref commits) == 1 && loseFirstReply)
                h.Remote.BulkEndpoint.DropAll();
        };
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = h.Mac.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) =>
        {
            if (h.Mac.Fixture.State("engine_message", "message") == "failed") settled.TrySetResult();
        });
        await using var relay = new MachineRelay(h.Mac.Runtime, h.Mac.Forwarder!, h.Connections, h.Mac.Topics, h.Mac.Fixture.Time, h.MirrorLog);
        try
        {
            relay.Start();
            try { await settled.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException error) { throw new InvalidOperationException(h.MirrorLog.ToString(), error); }
            Assert.Equal(2, prepares); Assert.Equal(2, commits); Assert.Empty(delivery.Sent);
            Assert.Equal(0, h.Remote.Fixture.Count("engine_message"));
            Assert.Equal(2, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%' AND json_extract(result_json,'$.error')='stale_epoch'"));
            Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%/root'"));
            Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%/retry'"));
            Assert.Equal("stale_epoch", h.Mac.Fixture.Text("SELECT reason FROM engine_message WHERE id='message'"));
            Assert.Equal(1, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-outcome/v1/%'"));
        }
        finally { h.Mac.Topics.Unsubscribe(sub); }
    }


    private static async Task<EngineMirror.LiveMirrorBinding> ReadyBindingAsync(EngineMirror mirror)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaughtUp() => ready.TrySetResult();
        mirror.CaughtUp += CaughtUp;
        try
        {
            if (await mirror.CaptureLiveBindingAsync(default) is { } current) return current;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Assert.IsType<EngineMirror.LiveMirrorBinding>(await mirror.CaptureLiveBindingAsync(default));
        }
        finally { mirror.CaughtUp -= CaughtUp; }
    }

}
