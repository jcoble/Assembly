using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Reading;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Relays;

namespace Assembly.Tests.Engine.Relays;

public sealed class RelayTurnReplyTargetTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    [InlineData("Exact 世界😀\0after_nul")]
    public async Task Proven_reply_uses_same_parts_with_zero_body_chunks_and_replays_original_receipt_and_expiry(string body)
    {
        using var target = new EngineFixture(); target.SeedSession("parent");
        using var source = RelayTurnReplyTests.Source(body, out var id, target: target.Machine);
        var commands = new EngineCommands(target.Machine, target.Limits);
        Register(target, source.Machine, commands);
        var capture = new EngineReader(source.Db, source.Machine).RelaySource(new(id, target.Machine), default);
        var descriptor = RelayDigests.Describe(capture); var request = new RelaySendRequest(descriptor, 0);
        await using var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource();
        var call = new CallContext(default, "bulk", closed.Token, Peer: new OwnerPeer());
        string from, to;
        using (var operation = parts.Enter(call))
        {
            from = operation.PublishSource(capture).Handle;
            var probe = target.Db.Writer.Write("test.probe", scope => commands.ProbeRelaySend(scope, request));
            to = operation.PublishTarget(request, probe).Transfer!;
        }
        var bodyChunks = 0;
        foreach (var kind in new[] { RelayPartKind.Body, RelayPartKind.SenderLabel })
        {
            var total = kind == RelayPartKind.Body ? descriptor.Body.Utf8Bytes : descriptor.SenderLabel.Utf8Bytes;
            for (long offset = 0; offset < total;)
            {
                using var operation = parts.Enter(call);
                var chunk = await operation.ReadSource(new(from, kind, offset));
                await operation.Append(new(to, kind, offset, chunk.Data));
                if (kind == RelayPartKind.Body) bodyChunks++;
                offset = chunk.Next;
            }
        }
        if (body.Length == 0) Assert.Equal(0, bodyChunks);
        PreparedRelaySend prepared;
        using (var operation = parts.Enter(call)) prepared = await operation.Admit(new(request, to));
        var reads = 0; var policy = new MessageExpiryPolicy(_ => { reads++; return 7; });
        commands = new EngineCommands(target.Machine, target.Limits, expiryPolicy: policy);
        var first = target.Db.Writer.Write("test.reply", scope => commands.ApplyRelaySend(scope, request, prepared, 1000));
        Assert.True(first.First!.Applied, first.First.Error); Assert.False(first.Unknown); Assert.True(first.DestinationPresent);
        Assert.Equal(body, target.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message", r => RelayHash.Utf8.GetString(r.Blob(0)), default));
        Assert.Equal(capture.SenderLabel, target.Db.QueryFirst("SELECT CAST(sender_label AS BLOB) FROM engine_message", r => RelayHash.Utf8.GetString(r.Blob(0)), default));
        Assert.Equal(421000, target.Number("SELECT expires_ms FROM engine_message")); Assert.Equal(1, reads);
        target.Db.Writer.Write("test.change", scope => scope.Execute("UPDATE engine_session SET epoch=4,reserved_epoch=5,title='changed' WHERE id='parent'"));
        target.Reopen();
        var replay = target.Db.Writer.Write("test.replay", scope => commands.ApplyRelaySend(scope, request, null, 10000));
        Assert.Equal(first.First, replay.First); Assert.False(replay.Unknown); Assert.True(replay.DestinationPresent);
        Assert.Equal(1, reads); Assert.Equal(1, target.Count("engine_message")); Assert.Equal(1, target.Count("engine_outbox"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_changed_foreign_child_proof_prevents_new_reply_insertion(bool corruptMarker)
    {
        using var target = new EngineFixture(); target.SeedSession("parent");
        using var source = RelayTurnReplyTests.Source("body", out var id, target: target.Machine);
        var commands = new EngineCommands(target.Machine, target.Limits); Register(target, source.Machine, commands);
        var descriptor = RelayDigests.Describe(new EngineReader(source.Db, source.Machine).RelaySource(new(id, target.Machine), default));
        target.Db.Writer.Write("test.damage", scope => scope.Execute(corruptMarker
            ? "UPDATE engine_command_receipt SET fingerprint='corrupt' WHERE command_id LIKE 'foreign-parent/v1/%'"
            : "UPDATE engine_session SET parent_id=NULL WHERE id='child'"));
        var result = target.Db.Writer.Write("test.reply", scope => commands.ApplyRelaySend(scope, new(descriptor, 0),
            PreparedRelaySend.Create(descriptor, RelayHash.Utf8.GetBytes("body"), RelayHash.Utf8.GetBytes("Test (child)")), 1000));
        Assert.False(result.First?.Applied ?? false); Assert.Equal(0, target.Count("engine_message")); Assert.Equal(0, target.Count("engine_outbox"));
    }

    [Fact]
    public void Actual_reply_destination_outcome_settles_ended_source_once_after_reopen()
    {
        using var target = new EngineFixture(); target.SeedSession("parent");
        using var source = RelayTurnReplyTests.Source("", out var id, target: target.Machine);
        var commands = new EngineCommands(target.Machine, target.Limits); Register(target, source.Machine, commands);
        var capture = new EngineReader(source.Db, source.Machine).RelaySource(new(id, target.Machine), default);
        var descriptor = RelayDigests.Describe(capture);
        var prepared = PreparedRelaySend.Create(descriptor, [], RelayHash.Utf8.GetBytes(capture.SenderLabel));
        Assert.True(target.Db.Writer.Write("test.reply", scope => commands.ApplyRelaySend(scope, new(descriptor, 0), prepared, 1000)).First!.Applied);
        var outcome = new MessageOutcome("next_turn", "provider-turn", null);
        Assert.True(target.Apply(outcome, id, version: target.Number("SELECT version FROM engine_message"), actor: new(ActorKind.System, "adapter")).Applied);
        var ack = target.Db.Writer.Write("test.ack", scope => commands.ReadRelayAck(scope, descriptor));
        source.Db.Writer.Write("test.end", scope => scope.Execute("UPDATE engine_session SET state='ended',title='renamed' WHERE id='child'"));
        var sourceCommands = new EngineCommands(source.Machine, source.Limits);
        var args = new RelayOutcomeArgs(target.Machine, descriptor, descriptor.IdentitySha256, descriptor.Fence.MessageVersion,
            descriptor.Fence.RecipientStubEpoch, outcome, ack);
        var result = source.Db.Writer.Write("test.outcome", scope => sourceCommands.ApplyRelayOutcome(scope, args, 2000));
        Assert.True(result.Applied); Assert.Equal("next_turn", source.State("engine_message", id)); Assert.Equal(0, source.Count("engine_outbox"));
        source.Reopen();
        Assert.Equal(result, source.Db.Writer.Write("test.replay", scope => sourceCommands.ApplyRelayOutcome(scope, args, 3000)));
        Assert.Equal(1, source.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-outcome/v1/%'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Typed_reply_receipt_and_effect_assertion_faults_roll_back_the_whole_supplied_writer(bool corruptAttempt)
    {
        using var target = new EngineFixture(); target.SeedSession("parent");
        using var source = RelayTurnReplyTests.Source("", out var id, target: target.Machine);
        var commands = new EngineCommands(target.Machine, target.Limits); Register(target, source.Machine, commands);
        var capture = new EngineReader(source.Db, source.Machine).RelaySource(new(id, target.Machine), default);
        var descriptor = RelayDigests.Describe(capture); var before = target.Count("engine_command_receipt");
        target.Db.Writer.Write("test.fault", scope => scope.Execute(corruptAttempt
            ? "CREATE TRIGGER corrupt_attempt AFTER UPDATE OF attempt ON engine_message WHEN NEW.attempt=1 BEGIN UPDATE engine_message SET attempt=0 WHERE id=NEW.id; END"
            : "CREATE TRIGGER fail_receipt BEFORE INSERT ON engine_command_receipt WHEN NEW.command_id LIKE 'relay-send/v1/%' BEGIN SELECT RAISE(ABORT,'receipt'); END"));
        var prepared = PreparedRelaySend.Create(descriptor, [], RelayHash.Utf8.GetBytes(capture.SenderLabel));
        Assert.NotNull(Record.Exception(() => target.Db.Writer.Write("test.reply", scope => commands.ApplyRelaySend(scope, new(descriptor, 0), prepared, 1000))));
        Assert.Equal(before, target.Count("engine_command_receipt")); Assert.Equal(0, target.Count("engine_message")); Assert.Equal(0, target.Count("engine_outbox")); Assert.Empty(target.Events());
    }

    [Fact]
    public void Same_message_bytes_from_an_ordinary_guid_receipt_do_not_prove_a_private_relay_acceptance()
    {
        using var target = new EngineFixture(); target.SeedSession("parent");
        using var source = RelayTurnReplyTests.Source("body", out var id, target: target.Machine);
        var commands = new EngineCommands(target.Machine, target.Limits); Register(target, source.Machine, commands);
        var descriptor = RelayDigests.Describe(new EngineReader(source.Db, source.Machine).RelaySource(new(id, target.Machine), default));
        target.Db.Writer.Write("test.stub-title", scope => scope.Execute("UPDATE engine_session SET title='Test' WHERE id='child'"));
        Assert.True(target.Apply(new SendMessage("child", "parent", "body", "prompt"), id, actor: new(ActorKind.System, "machine:" + source.Machine)).Applied);
        Assert.Equal(0, target.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%'"));
        var ack = target.Db.Writer.Write("test.ack", scope => commands.ReadRelayAck(scope, descriptor));
        Assert.True(ack.Unknown); Assert.Null(ack.First); Assert.Null(ack.Retry);
        Assert.Equal("unknown", target.Db.Writer.Write("test.probe", scope => commands.ProbeRelaySend(scope, new(descriptor, 0))).Status);
        Assert.Equal(1, target.Count("engine_message")); Assert.Equal(0, target.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'relay-send/v1/%'"));
    }

    private static void Register(EngineFixture target, string source, EngineCommands commands)
    {
        var prepared = commands.PrepareForeignAncestry(new(source, "child", [new("parent", target.Machine, "codex", "Parent", null), new("child", source, "codex", "Child", "parent")]));
        Assert.True(target.Db.Writer.Write("test.ancestry", scope => commands.ApplyForeignAncestryBatch(scope, prepared)).Applied);
    }
}
