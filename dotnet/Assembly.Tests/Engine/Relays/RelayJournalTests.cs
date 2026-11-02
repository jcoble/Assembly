using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Engine.Reading;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Relays;

public sealed class RelayJournalTests
{
    [Fact]
    public void Typed_send_stores_full_body_and_original_label_and_reads_policy_only_for_fresh_insert()
    {
        using var target = new EngineFixture();
        using var source = RelaySourceTests.Source(40000, out var body, out var label, target.Machine);
        var capture = new EngineReader(source.Db, source.Machine).RelaySource(new("message", target.Machine), default);
        var descriptor = RelayDigests.Describe(capture);
        Register(target, source.Machine);
        var reads = 0; var minutes = 7;
        var commands = new EngineCommands(target.Machine, target.Limits, expiryPolicy: new(_ => { reads++; return minutes; }));
        var request = new RelaySendRequest(descriptor, 0);
        var prepared = PreparedRelaySend.Create(descriptor, Encoding.UTF8.GetBytes(body), Encoding.UTF8.GetBytes(label));
        var ack = target.Db.Writer.Write("test.relay", scope => commands.ApplyRelaySend(scope, request, prepared, 1000));
        Assert.True(ack.First!.Applied);
        Assert.True(ack.DestinationPresent);
        Assert.False(ack.Unknown);
        Assert.Equal(1, reads);
        Assert.Equal(421000, target.Number("SELECT expires_ms FROM engine_message"));
        Assert.Equal(Encoding.UTF8.GetBytes(body), target.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message", r => r.Blob(0), default));
        Assert.Equal(Encoding.UTF8.GetBytes(label), target.Db.QueryFirst("SELECT CAST(sender_label AS BLOB) FROM engine_message", r => r.Blob(0), default));
        Assert.Equal("session", target.Text("SELECT sender_kind FROM engine_message"));
        var payload = target.Text("SELECT payload_json FROM engine_outbox WHERE kind='send'")!;
        var delivery = JsonSerializer.Deserialize(payload, EngineJson.Default.DeliverMessage)!;
        Assert.Equal(MessageText.Prompt(label, body), delivery.Text);
        Assert.Equal(label, delivery.SenderLabel);
        minutes = 99;
        target.Reopen();
        var replay = target.Db.Writer.Write("test.replay", scope => commands.ApplyRelaySend(scope, request, null, 2000));
        Assert.Equal(ack.First, replay.First);
        Assert.Equal(1, reads);
        Assert.Equal(1, target.Count("engine_message"));
        Assert.Equal(1, target.Count("engine_outbox"));
        Assert.StartsWith("relay-send-fp/v1/0/", target.Text("SELECT fingerprint FROM engine_command_receipt WHERE command_id=$key", new SqlArg("$key", ack.FirstKey)));
    }

    [Fact]
    public void Stale_epoch_receipt_reopens_without_upload_and_binds_every_immutable_descriptor_field()
    {
        using var target = new EngineFixture();
        using var source = RelaySourceTests.Source(50, out var body, out var label, target.Machine);
        var descriptor = RelayDigests.Describe(new EngineReader(source.Db, source.Machine).RelaySource(new("message", target.Machine), default));
        Register(target, source.Machine, epoch: 8);
        var commands = new EngineCommands(target.Machine, target.Limits);
        var request = new RelaySendRequest(descriptor, 7);
        var prepared = PreparedRelaySend.Create(descriptor, Encoding.UTF8.GetBytes(body), Encoding.UTF8.GetBytes(label));
        var first = target.Db.Writer.Write("test.stale", scope => commands.ApplyRelaySend(scope, request, prepared, 1000));
        Assert.Equal("stale_epoch", first.First!.Error);
        Assert.Equal(8, first.First.Epoch);
        Assert.Equal(0, target.Count("engine_message"));
        target.Reopen();
        var replay = target.Db.Writer.Write("test.replay", scope => commands.ApplyRelaySend(scope, request, null, 2000));
        Assert.Equal(first.First, replay.First);
        foreach (var changed in new[] { descriptor with { FromSessionId = "different" }, descriptor with { ToSessionId = "different" },
            descriptor with { Mode = "interrupt" }, descriptor with { Body = descriptor.Body with { Utf8Bytes = descriptor.Body.Utf8Bytes + 1 } } })
        {
            var invalid = target.Db.Writer.Write("test.changed", scope => commands.ReadRelayAck(scope, changed));
            Assert.True(invalid.Unknown);
        }
        var retry = request with { PreparedRecipientEpoch = 8, PredecessorRefusalKey = first.FirstKey };
        var accepted = target.Db.Writer.Write("test.retry", scope => commands.ApplyRelaySend(scope, retry, prepared, 2000));
        Assert.True(accepted.Retry!.Applied);
        Assert.Equal(1, target.Count("engine_message"));
        Assert.Equal(2, target.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%'"));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("label")]
    [InlineData("receipt")]
    [InlineData("reservation")]
    public void Insert_label_receipt_and_effect_assertion_faults_roll_back_all_typed_state(string fault)
    {
        using var target = new EngineFixture();
        using var source = RelaySourceTests.Source(50, out var body, out var label, target.Machine);
        var descriptor = RelayDigests.Describe(new EngineReader(source.Db, source.Machine).RelaySource(new("message", target.Machine), default));
        Register(target, source.Machine);
        var before = target.Count("engine_command_receipt");
        var trigger = fault switch
        {
            "body" => "CREATE TRIGGER fail_body BEFORE INSERT ON engine_message BEGIN SELECT RAISE(ABORT,'body'); END",
            "label" => "CREATE TRIGGER fail_label BEFORE UPDATE OF sender_label ON engine_message BEGIN SELECT RAISE(ABORT,'label'); END",
            "receipt" => "CREATE TRIGGER fail_receipt BEFORE INSERT ON engine_command_receipt WHEN NEW.command_id LIKE 'relay-send/v1/%' BEGIN SELECT RAISE(ABORT,'receipt'); END",
            _ => "CREATE TRIGGER corrupt_attempt AFTER UPDATE OF attempt ON engine_message WHEN NEW.attempt=1 BEGIN UPDATE engine_message SET attempt=0 WHERE id=NEW.id; END"
        };
        target.Db.Writer.Write("test.fault", scope => scope.Execute(trigger));
        var commands = new EngineCommands(target.Machine, target.Limits);
        var prepared = PreparedRelaySend.Create(descriptor, Encoding.UTF8.GetBytes(body), Encoding.UTF8.GetBytes(label));
        var error = Record.Exception(() => target.Db.Writer.Write("test.send", scope => commands.ApplyRelaySend(scope, new(descriptor, 0), prepared, 1000)));
        Assert.NotNull(error);
        Assert.Equal(0, target.Count("engine_message")); Assert.Equal(0, target.Count("engine_outbox"));
        Assert.Equal(before, target.Count("engine_command_receipt")); Assert.Empty(target.Events());
    }

    [Fact]
    public void Actual_target_terminal_evidence_settles_source_once_with_private_outcome_and_no_local_effect()
    {
        using var target = new EngineFixture();
        using var source = RelaySourceTests.Source(50, out var body, out var label, target.Machine);
        var descriptor = RelayDigests.Describe(new EngineReader(source.Db, source.Machine).RelaySource(new("message", target.Machine), default));
        Register(target, source.Machine);
        var commands = new EngineCommands(target.Machine, target.Limits);
        var prepared = PreparedRelaySend.Create(descriptor, Encoding.UTF8.GetBytes(body), Encoding.UTF8.GetBytes(label));
        var first = target.Db.Writer.Write("test.send", scope => commands.ApplyRelaySend(scope, new(descriptor, 0), prepared, 1000));
        Assert.True(first.DestinationPresent);
        var version = target.Number("SELECT version FROM engine_message WHERE id='message'");
        var outcome = new MessageOutcome("next_turn", "provider-turn", null);
        Assert.True(target.Apply(outcome, "message", version: version, actor: new(ActorKind.System, "adapter")).Applied);
        var actualAck = target.Db.Writer.Write("test.ack", scope => commands.ReadRelayAck(scope, descriptor));
        Assert.Equal("next_turn", actualAck.Destination!.State);
        var sourceCommands = new EngineCommands(source.Machine, source.Limits);
        var args = new RelayOutcomeArgs(target.Machine, descriptor, descriptor.IdentitySha256,
            descriptor.Fence.MessageVersion, descriptor.Fence.RecipientStubEpoch, outcome, actualAck);
        var result = source.Db.Writer.Write("test.outcome", scope => sourceCommands.ApplyRelayOutcome(scope, args, 2000));
        Assert.True(result.Applied);
        Assert.Equal("next_turn", source.State("engine_message", "message"));
        Assert.Equal(0, source.Count("engine_outbox"));
        source.Reopen();
        Assert.Equal(result, source.Db.Writer.Write("test.replay", scope => sourceCommands.ApplyRelayOutcome(scope, args, 3000)));
        Assert.Equal(1, source.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-outcome/v1/%'"));
    }

    private static void Register(EngineFixture target, string source, long epoch = 0)
    {
        target.SeedSession("recipient", epoch: epoch);
        Assert.True(target.Apply(new RegisterForeignSession(source, "codex", "MUTABLE TARGET STUB TITLE"), "sender",
            actor: new(ActorKind.System, "machine:" + source)).Applied);
    }
}
