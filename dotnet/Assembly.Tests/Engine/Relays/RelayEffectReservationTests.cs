using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Reading;
using Assembly.Engine.Persistence;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Relays;

public sealed class RelayEffectReservationTests
{
    [Fact]
    public void New_relay_releases_older_eligible_message_and_replays_without_a_second_reservation()
    {
        using var target = new EngineFixture();
        using var source = RelaySourceTests.Source(50, out var body, out var label, target.Machine, labelChars: 8);
        target.SeedSession("recipient", state: "running");
        Assert.True(target.Apply(new RegisterForeignSession(source.Machine, "codex", "Sender"), "sender",
            actor: new(ActorKind.System, "machine:" + source.Machine)).Applied);
        Assert.True(target.Apply(new SendMessage(null, "recipient", "older body", "prompt"), "older").Applied);
        Assert.Equal(0, target.Count("engine_outbox"));
        Assert.True(target.Apply(new ReportCapabilities(true), "recipient", actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal(0, target.Count("engine_outbox"));
        var descriptor = RelayDigests.Describe(new EngineReader(source.Db, source.Machine).RelaySource(new("message", target.Machine), default));
        var prepared = PreparedRelaySend.Create(descriptor, Encoding.UTF8.GetBytes(body), Encoding.UTF8.GetBytes(label));
        var commands = new EngineCommands(target.Machine, target.Limits);
        var ack = target.Db.Writer.Write("test.relay", scope => commands.ApplyRelaySend(scope, new(descriptor, 0), prepared, 1001));
        Assert.True(ack.First!.Applied);
        Assert.True(ack.DestinationPresent);
        Assert.Equal(1, target.Number("SELECT in_flight FROM engine_message WHERE id='older'"));
        Assert.Equal(1, target.Number("SELECT attempt FROM engine_message WHERE id='older'"));
        Assert.Equal(0, target.Number("SELECT in_flight FROM engine_message WHERE id='message'"));
        Assert.Equal(0, target.Number("SELECT attempt FROM engine_message WHERE id='message'"));
        Assert.Equal("send/older/1", target.Text("SELECT id FROM engine_outbox"));
        target.Reopen();
        var replay = target.Db.Writer.Write("test.replay", scope => commands.ApplyRelaySend(scope, new(descriptor, 0), null, 2000));
        Assert.Equal(ack.First, replay.First);
        Assert.Equal(1, target.Count("engine_outbox"));
        Assert.Equal(1, target.Number("SELECT attempt FROM engine_message WHERE id='older'"));
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("recipient")]
    [InlineData("machine")]
    public void Corrupt_older_reservation_rolls_back_new_relay_and_all_reservation_changes(string fault)
    {
        using var target = new EngineFixture();
        using var source = RelaySourceTests.Source(50, out var body, out var label, target.Machine, labelChars: 8);
        target.SeedSession("recipient", state: "running");
        target.SeedSession("other");
        Assert.True(target.Apply(new RegisterForeignSession(source.Machine, "codex", "Sender"), "sender",
            actor: new(ActorKind.System, "machine:" + source.Machine)).Applied);
        Assert.True(target.Apply(new SendMessage(null, "recipient", "older body", "prompt"), "older").Applied);
        Assert.True(target.Apply(new ReportCapabilities(true), "recipient", actor: new(ActorKind.System, "adapter")).Applied);
        var receipts = target.Count("engine_command_receipt");
        var events = target.Count("engine_event");
        var mutation = fault switch
        {
            "attempt" => "attempt=0",
            "recipient" => "to_session_id='other'",
            _ => "machine='wrong-machine'"
        };
        target.Db.Writer.Write("test.corrupt-reservation", scope => scope.Execute(
            "CREATE TRIGGER corrupt_reservation AFTER UPDATE OF attempt ON engine_message WHEN NEW.id='older' AND NEW.attempt=1 BEGIN UPDATE engine_message SET " + mutation + " WHERE id=NEW.id; END"));
        var descriptor = RelayDigests.Describe(new EngineReader(source.Db, source.Machine).RelaySource(new("message", target.Machine), default));
        var prepared = PreparedRelaySend.Create(descriptor, Encoding.UTF8.GetBytes(body), Encoding.UTF8.GetBytes(label));
        var commands = new EngineCommands(target.Machine, target.Limits);
        Assert.Throws<InvalidOperationException>(() => target.Db.Writer.Write("test.relay", scope =>
            commands.ApplyRelaySend(scope, new(descriptor, 0), prepared, 1001)));
        Assert.Equal(1, target.Count("engine_message"));
        Assert.Equal(0, target.Count("engine_outbox"));
        Assert.Equal(receipts, target.Count("engine_command_receipt"));
        Assert.Equal(events, target.Count("engine_event"));
        Assert.Equal(0, target.Number("SELECT in_flight FROM engine_message WHERE id='older'"));
        Assert.Equal(0, target.Number("SELECT attempt FROM engine_message WHERE id='older'"));
        Assert.Equal("recipient", target.Text("SELECT to_session_id FROM engine_message WHERE id='older'"));
        Assert.Equal(target.Machine, target.Text("SELECT machine FROM engine_message WHERE id='older'"));
    }

    [Theory]
    [InlineData(false, "duplicate")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "payload-recipient")]
    [InlineData(false, "effect-session")]
    [InlineData(false, "effect-attempt")]
    [InlineData(false, "other-kind")]
    [InlineData(false, "other-stop")]
    public void Fixed_assertion_rejects_duplicate_or_mismatched_effects(bool outcome, string fault)
    {
        using var target = new EngineFixture();
        target.SeedSession("recipient");
        Assert.True(target.Apply(new SendMessage(null, "recipient", "body", "prompt"), "older").Applied);
        var payload = JsonSerializer.Deserialize(target.Text("SELECT payload_json FROM engine_outbox")!, EngineJson.Default.DeliverMessage)!;
        var effect = new EffectDraft("send/older/1", "send", "recipient", null,
            JsonSerializer.SerializeToElement(payload, EngineJson.Default.DeliverMessage));
        var context = new CommandContext(Guid.NewGuid(), target.Machine, new(ActorKind.System, "relay"), "older", 0, 0, 1001);
        var assertion = outcome ? RelayEffectAssertion.Outcome("older") : RelayEffectAssertion.Send("older");
        target.Db.Writer.Write("test.valid-assertion", scope =>
        {
            assertion.Validate(scope, context, [effect]);
            return 0;
        });
        var invalid = fault switch
        {
            "payload-recipient" => effect with { Payload = JsonSerializer.SerializeToElement(payload with { ToSessionId = "other" }, EngineJson.Default.DeliverMessage) },
            "effect-session" => effect with { SessionId = "other" },
            "effect-attempt" => effect with { Id = "send/older/2" },
            "other-kind" => effect with { Kind = "start" },
            "other-stop" => effect with { Kind = "stop", Id = "message-stop/unrelated" },
            _ => effect
        };
        Assert.Throws<InvalidOperationException>(() => target.Db.Writer.Write("test.invalid-assertion", scope =>
        {
            scope.Execute("UPDATE engine_message SET reason='uncommitted' WHERE id='older'");
            assertion.Validate(scope, context, fault == "duplicate" ? [effect, invalid] : [invalid]);
            return 0;
        }));
        Assert.Null(target.Text("SELECT reason FROM engine_message WHERE id='older'"));
    }

}
