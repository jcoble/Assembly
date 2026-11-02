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
    public async Task One_pair_arrives_in_original_created_order_across_pushed_settlements(bool remoteSource)
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
        Assert.True(target.Fixture.Apply(new RegisterForeignSession(source.Runtime.Machine, "codex", "Test"), sender,
            actor: new(ActorKind.System, "machine:" + source.Runtime.Machine)).Applied);
        string[] ids = ["z-first", "a-second", "m-third"];
        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i]; var now = 1000 + i;
            var prompt = PreparedHelperPrompt.Prepare(new(sender, recipient, id + "\0body", "prompt"), "Test", 0);
            Assert.True(source.Fixture.Db.Writer.Write("test.source", scope => source.Runtime.Commands.ApplyPreparedHelperPrompt(scope,
                new(Guid.NewGuid(), source.Runtime.Machine, new(ActorKind.Session, sender), id, 0, 0, now), prompt)).Applied);
        }
        await h.StartMirror();
        var settled = ids.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var sub = source.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) =>
        {
            for (var i = 0; i < ids.Length; i++)
                if (source.Fixture.State("engine_message", ids[i]) == "next_turn") settled[i].TrySetResult();
        });
        await using var relay = new MachineRelay(h.Mac.Runtime, h.Mac.Forwarder!, h.Connections, h.Mac.Topics, h.Mac.Fixture.Time, h.MirrorLog);
        try
        {
            relay.Start();
            try
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    await settled[i].Task.WaitAsync(TimeSpan.FromSeconds(20));
                    if (i < ids.Length - 1)
                        Assert.True(target.Fixture.Apply(new SetSessionState("idle", null, null), recipient,
                            target.Fixture.Number("SELECT version FROM engine_session WHERE id=$id", new Assembly.Application.Common.Database.SqlArg("$id", recipient)),
                            actor: new(ActorKind.System, "adapter")).Applied);
                }
            }
            catch (TimeoutException error) { throw new InvalidOperationException(h.MirrorLog.ToString(), error); }
            Assert.Equal(ids, delivery.Sent.Select(message => message.MessageId));
            Assert.Equal(3, target.Fixture.Count("engine_message"));
            Assert.Equal(3, target.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%'"));
            Assert.Equal(3, source.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-outcome/v1/%'"));
        }
        finally { source.Topics.Unsubscribe(sub); }
    }
}
