using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;

namespace Assembly.Tests.Engine.Workflows;

public sealed partial class WorkflowReceiptSubmissionTests
{
    [Fact]
    public void Queued_workflow_reminder_expires_instead_of_binding_new_worker_epoch()
    {
        using var f = new Fixture();
        Assert.True(f.Core.Apply(new SendMessage(null, "worker", "Earlier owner message", "prompt"), "blocker", epoch: 7).Applied);
        Assert.True(ApplyRelease(f, Release(f, "before-reminder")).Applied);
        var reminder = f.Core.Text("SELECT id FROM engine_message WHERE sender_kind='system'")!;
        Assert.Equal(0, f.Core.Number("SELECT in_flight FROM engine_message WHERE id=$id", new SqlArg("$id", reminder)));
        CompleteNextTurn(f, "blocker"); // Running without steering keeps the reminder queued.
        f.Core.Db.Writer.Write("test.resumed-epoch", scope => scope.Execute("UPDATE engine_session SET epoch=8,state='idle' WHERE id='worker'"));
        f.Core.Db.Writer.Write("test.release-new-epoch", scope =>
        {
            var context = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.System, "worker"), "worker", 0, 8, 1003);
            List<EffectDraft> effects = [];
            var events = MessageRelease.Next(scope, context, "worker", effects);
            return EngineJournal.Commit(scope, context, 0, 8, events, effects);
        });
        Assert.Equal("failed", f.Core.State("engine_message", reminder));
        Assert.Equal("moved", f.Core.Text("SELECT reason FROM engine_message WHERE id=$id", new SqlArg("$id", reminder)));
        Assert.Equal(7, f.Core.Number("SELECT held_epoch FROM engine_message WHERE id=$id", new SqlArg("$id", reminder)));
        Assert.Equal(7, f.Core.Number("SELECT target_epoch FROM engine_message WHERE id=$id", new SqlArg("$id", reminder)));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send' AND json_extract(payload_json,'$.messageId')=$id", new SqlArg("$id", reminder)));
    }

    [Fact]
    public void Old_workflow_reminder_effect_cannot_bind_resumed_epoch_even_when_already_done()
    {
        using var f = new Fixture(); Assert.True(ApplyRelease(f, Release(f, "original")).Applied);
        var id = f.Core.Text("SELECT id FROM engine_outbox WHERE kind='send'")!;
        var oldEffect = EffectTransitions.Read(f.Core.Db, id, default)!;
        using (var scope = f.Core.Db.OpenSnapshot(default)) Assert.NotNull(MessageDelivery.Read(scope, oldEffect));
        DeliverReminder(f, "reminder-response");
        f.Core.Db.Writer.Write("test.resumed-epoch", scope => scope.Execute("UPDATE engine_session SET epoch=8 WHERE id='worker'"));
        using var current = f.Core.Db.OpenSnapshot(default);
        Assert.Null(MessageDelivery.Read(current, oldEffect, allowDone: true));
    }

    [Fact]
    public void Ordinary_untargeted_framed_message_keeps_existing_delivery_behavior_across_epoch_change()
    {
        using var f = new Fixture();
        Assert.True(f.Core.Apply(new SendMessage(null, "worker", "Ordinary owner message", "prompt"), "ordinary", epoch: 7).Applied);
        var effect = EffectTransitions.Read(f.Core.Db, f.Core.Text("SELECT id FROM engine_outbox WHERE kind='send'")!, default)!;
        f.Core.Db.Writer.Write("test.ordinary-epoch", scope => scope.Execute("UPDATE engine_session SET epoch=8 WHERE id='worker'"));
        using var current = f.Core.Db.OpenSnapshot(default);
        Assert.Equal(8, MessageDelivery.Read(current, effect)!.Epoch);
        Assert.Null(f.Core.Text("SELECT target_epoch FROM engine_message WHERE id='ordinary'"));
    }

    private static void CompleteNextTurn(Fixture f, string messageId)
    {
        var id = f.Core.Text("SELECT id FROM engine_outbox WHERE kind='send' AND json_extract(payload_json,'$.messageId')=$id", new SqlArg("$id", messageId))!;
        var claim = f.Core.Apply(new ClaimEffect(), id, actor: new(ActorKind.System, "worker")); Assert.True(claim.Applied, claim.Error);
        var receipt = new EffectReceipt(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new MessageEffectData("next_turn", "fixture-turn", null), EngineJson.Default.MessageEffectData), null);
        var result = f.Core.Apply(new CompleteEffect(receipt), id, version: claim.Version, epoch: 7, actor: new(ActorKind.System, "worker"));
        Assert.True(result.Applied, result.Error);
    }
}
