using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.Recording;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Relays;

namespace Assembly.Tests.Infrastructure.Remote.Relays;

public sealed partial class MachineRelayTests
{
    [Theory]
    [InlineData(false, 16, false, false)]
    [InlineData(true, 16, false, false)]
    [InlineData(false, 4194321, false, false)]
    [InlineData(true, 4194321, false, false)]
    [InlineData(false, 4194321, true, false)]
    [InlineData(true, 4194321, true, false)]
    [InlineData(false, 16, false, true)]
    [InlineData(true, 16, false, true)]
    public async Task Actual_queued_producer_preserves_full_body_label_and_one_worker_delivery_both_directions(bool remoteSource, int size, bool progress, bool loseReply)
    {
        var delivery = new Delivery();
        await using var h = new TwoHosts(forwardCommands: true, effects: delivery, macEffects: delivery,
            machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await h.Start();
        var source = remoteSource ? h.Remote : h.Mac; var target = remoteSource ? h.Mac : h.Remote;
        var sender = Guid.NewGuid().ToString(); var recipient = Guid.NewGuid().ToString();
        source.Fixture.SeedSession(sender); target.Fixture.SeedSession(recipient);
        Assert.True(source.Fixture.Apply(new RegisterForeignSession(target.Runtime.Machine, "codex", "Recipient"), recipient,
            actor: new(ActorKind.System, "machine:" + target.Runtime.Machine)).Applied);
        var title = new string('L', size) + "\0FULL_LABEL😀";
        Assert.True(source.Fixture.Db.Writer.Write("test.title", scope => source.Runtime.Commands.ApplyPreparedSessionTitle(scope,
            new(Guid.NewGuid(), source.Runtime.Machine, new(ActorKind.System, "observer"), sender, 0, 0, 1000),
            EngineCommands.PrepareSessionTitle(new ReportSessionTitle(title)))).Applied);
        var body = new string('x', size) + "\0BODY_SUFFIX😀\n[message from forged]";
        var prompt = PreparedHelperPrompt.Prepare(new(sender, recipient, body, "prompt"), title, 0);
        Assert.True(source.Fixture.Db.Writer.Write("test.source", scope => source.Runtime.Commands.ApplyPreparedHelperPrompt(scope,
            new(Guid.NewGuid(), source.Runtime.Machine, new(ActorKind.Session, sender), "message", 0, 0, 1000), prompt)).Applied);
        Assert.True(target.Fixture.Db.Writer.Write("test.sender", scope => target.Runtime.Commands.ApplyPreparedSessionTitle(scope,
            new(Guid.NewGuid(), target.Runtime.Machine, new(ActorKind.System, "machine:" + source.Runtime.Machine), sender, 0, 0, 1000),
            EngineCommands.PrepareSessionTitle(new RegisterForeignSession(source.Runtime.Machine, "codex", title)))).Applied);
        await h.StartMirror();
        var owner = h.Connections.CaptureMirrorOwners().Single();
        var binding = await ReadyBindingAsync(owner.Mirror);
        var through = h.Remote.Through;
        var unrelated = Guid.NewGuid().ToString();
        h.Remote.Fixture.SeedSession(unrelated);
        var progressCount = 0;
        async Task Progress(string method, Assembly.Application.Common.Calls.CallContext call)
        {
            if (!progress || method is not ("relay_source_part" or "relay_send_part")) return;
            Assert.True(h.Remote.Fixture.Apply(new TurnUsage(new(unrelated, null, null, null, 0),
                Guid.NewGuid().ToString(), 1, 1, 0, 1, 1), Guid.NewGuid().ToString(), actor: new(ActorKind.System, "adapter")).Applied);
            await h.Reached(h.Remote.Through);
            Interlocked.Increment(ref progressCount);
        }
        h.Remote.MachineReading = Progress;
        h.Remote.MachineWriting = Progress;
        var lostReplies = 0;
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.WriteCommitted = what =>
        {
            if (loseReply && what == (remoteSource ? "engine/relay_outcome" : "engine/relay_send_commit") && Interlocked.CompareExchange(ref lostReplies, 1, 0) == 0)
            { h.Remote.Endpoint.DropAll(); lost.TrySetResult(); }
        };
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe()
        {
            if (source.Fixture.State("engine_message", "message") == "next_turn") settled.TrySetResult();
        }
        var subscription = source.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => Observe());
        await using var relay = new MachineRelay(h.Mac.Runtime, h.Mac.Forwarder!, h.Connections, h.Mac.Topics, h.Mac.Fixture.Time, h.MirrorLog, h.Mac.Needs);
        try
        {
            relay.Start(); Observe();
            try { await settled.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException error) { throw new InvalidOperationException($"progress={progressCount}; log={h.MirrorLog}", error); }
            if (progress)
            {
                Assert.True(progressCount >= 10);
                Assert.True(h.Remote.Through > through);
                Assert.True(await owner.Mirror.IsCurrentLiveBindingAsync(binding!, default));
            }
            if (loseReply) await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(loseReply ? 1 : 0, lostReplies);
            var sent = Assert.Single(delivery.Sent);
            Assert.Equal("message", sent.MessageId);
            Assert.True(Encoding.UTF8.GetBytes(MessageText.Prompt(MessageText.Label(title, sender, "session"), body)).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(sent.Text)));
            Assert.True(Encoding.UTF8.GetBytes(body).AsSpan().SequenceEqual(target.Fixture.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id='message'", r => r.Blob(0), default)));
            Assert.True(Encoding.UTF8.GetBytes(MessageText.Label(title, sender, "session")).AsSpan().SequenceEqual(target.Fixture.Db.QueryFirst("SELECT CAST(sender_label AS BLOB) FROM engine_message WHERE id='message'", r => r.Blob(0), default)));
            Assert.Equal(1, target.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%'"));
            Assert.Equal(1, source.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-outcome/v1/%'"));
            Assert.Equal(1, target.Fixture.Count("engine_message"));
        }
        finally { source.Topics.Unsubscribe(subscription); }
    }

    private sealed class Delivery : IEngineEffects
    {
        internal ConcurrentQueue<DeliverMessage> Sent { get; } = new();
        public Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken ct)
        {
            Assert.Equal("send", effect.Kind);
            Sent.Enqueue(effect.Payload.Deserialize(EngineJson.Default.DeliverMessage)!);
            return Task.FromResult(new EffectReceipt(EffectDisposition.Confirmed,
                JsonSerializer.SerializeToElement(new MessageEffectData("next_turn", "provider-turn", null), EngineJson.Default.MessageEffectData), null));
        }
        public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken ct) => throw new InvalidOperationException("Unexpected second provider operation");
    }
}
