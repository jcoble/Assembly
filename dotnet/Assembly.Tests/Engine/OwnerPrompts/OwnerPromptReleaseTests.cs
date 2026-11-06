using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.OwnerPrompts;

public sealed class OwnerPromptReleaseTests
{
    [Theory]
    [InlineData("\0start")]
    [InlineData("mid\0dle")]
    [InlineData("end\0")]
    public void Raw_release_preserves_nul_text_order_epoch_and_original_receipt(string text)
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running", epoch: 7);
        var prompt = OwnerPromptFingerprint.Prepare(f.Machine, new("s", text, "C", ["two", "one"], null), default);
        var context = new CommandContext(OwnerPromptIdentity.CommandId(f.Machine, "s", "C"), f.Machine,
            new(ActorKind.Window, "owner"), "prompt/s/C", 0, 0, 1000);
        var accepted = f.Db.Writer.Write("test.owner", s => new EngineCommands(f.Machine, f.Limits).ApplyOwnerPrompt(s, context, prompt));
        Assert.True(accepted.Applied); Assert.Equal(1, accepted.Version); f.Reopen();
        Assert.True(f.Apply(new SetSessionState("idle", null, null), "s", f.Number("SELECT version FROM engine_session WHERE id='s'"), 7,
            new(ActorKind.System, "test")).Applied);
        var intent = JsonSerializer.Deserialize(f.Text("SELECT payload_json FROM engine_outbox WHERE kind='send'")!, EngineJson.Default.DeliverMessage)!;
        Assert.Equal(text, intent.Text); Assert.False(intent.Framed); Assert.Equal(7, intent.HeldEpoch);
        Assert.Equal(new[] { "two", "one" }, intent.AttachmentIds); Assert.Equal("C", intent.ClientId);
        Assert.Equal(2, f.Number("SELECT version FROM engine_message WHERE id='prompt/s/C'"));
        var outcome = f.Apply(new MessageOutcome("next_turn", "turn", null), "prompt/s/C", 2, 7, new(ActorKind.System, "test"));
        Assert.True(outcome.Applied, outcome.Error);
        Assert.Equal(accepted, f.Db.Writer.Write("test.replay", s => new EngineCommands(f.Machine, f.Limits).ApplyOwnerPrompt(s, context, prompt)));
        Assert.Equal(1, f.Count("engine_outbox"));
    }

    [Fact]
    public void Stale_unreserved_owner_fails_moved_and_next_current_prompt_releases()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running", epoch: 7);
        Assert.True(Accept(f, "old").Applied);
        f.Db.Writer.Write("test.ownership-move", s => s.Execute("UPDATE engine_session SET epoch=8 WHERE id='s'"));
        Assert.True(Accept(f, "current").Applied);
        Assert.Equal("failed", f.State("engine_message", "prompt/s/old"));
        Assert.Equal("moved", f.Text("SELECT reason FROM engine_message WHERE id='prompt/s/old'"));
        Assert.True(f.Apply(new SetSessionState("idle", null, null), "s", f.Number("SELECT version FROM engine_session WHERE id='s'"), 8,
            new(ActorKind.System, "test")).Applied);
        Assert.Equal("prompt/s/current", JsonSerializer.Deserialize(f.Text("SELECT payload_json FROM engine_outbox")!, EngineJson.Default.DeliverMessage)!.MessageId);
    }

    [Fact]
    public void Ending_fails_unreserved_raw_owner_but_preserves_uncertain_reservation()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running", epoch: 7);
        Assert.True(Accept(f, "pending").Applied); Assert.True(Accept(f, "uncertain").Applied);
        f.Db.Writer.Write("test.uncertain", s => s.Execute("UPDATE engine_message SET in_flight=1,attempt=1 WHERE client_id='uncertain'"));
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "s", f.Number("SELECT version FROM engine_session WHERE id='s'"), 7,
            new(ActorKind.System, "test")).Applied);
        Assert.Equal("failed", f.State("engine_message", "prompt/s/pending"));
        Assert.Equal("recipient_ended", f.Text("SELECT reason FROM engine_message WHERE client_id='pending'"));
        Assert.Equal("queued", f.State("engine_message", "prompt/s/uncertain"));
        Assert.Equal(1, f.Db.QueryFirst("SELECT in_flight FROM engine_message WHERE client_id='uncertain'", r => r.Long(0), default));
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Stale_captured_epoch_cannot_complete_an_uncertain_delivery_with_current_epoch()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running", epoch: 7);
        Assert.True(Accept(f, "uncertain").Applied);
        f.Db.Writer.Write("test.moved-inflight", s => s.Execute("UPDATE engine_message SET in_flight=1;UPDATE engine_session SET epoch=8"));
        var result = f.Apply(new MessageOutcome("next_turn", "turn", null), "prompt/s/uncertain", 1, 8, new(ActorKind.System, "test"));
        Assert.False(result.Applied); Assert.Equal("stale_epoch", result.Error);
        Assert.Equal("queued", f.State("engine_message", "prompt/s/uncertain"));
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message")); Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Running_capable_owner_prompt_uses_existing_steer_release_without_framing()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running", epoch: 7);
        f.Db.Writer.Write("test.capability", s => s.Execute("UPDATE engine_session SET can_steer=1 WHERE id='s'"));
        var accepted = Accept(f, "steer"); Assert.True(accepted.Applied); Assert.Equal(2, accepted.Version);
        var intent = JsonSerializer.Deserialize(f.Text("SELECT payload_json FROM engine_outbox")!, EngineJson.Default.DeliverMessage)!;
        Assert.Equal("steer", intent.How); Assert.Equal("text", intent.Text); Assert.False(intent.Framed); Assert.Equal(7, intent.HeldEpoch);
    }

    [Fact]
    public void Old_framed_effect_serialization_retains_exact_original_seven_fields()
    {
        var intent = new DeliverMessage("m", "s", "body", "label", "prompt", "c", 7);
        Assert.Equal("{\"messageId\":\"m\",\"toSessionId\":\"s\",\"text\":\"body\",\"senderLabel\":\"label\",\"how\":\"prompt\",\"clientId\":\"c\",\"recipientVersion\":7}", JsonSerializer.Serialize(intent, EngineJson.Default.DeliverMessage));
    }

    private static CommandResult Accept(EngineFixture f, string client)
    {
        var p = OwnerPromptFingerprint.Prepare(f.Machine, new("s", "text", client, [], null), default);
        return f.Db.Writer.Write("test.owner", s => new EngineCommands(f.Machine, f.Limits).ApplyOwnerPrompt(s,
            new(OwnerPromptIdentity.CommandId(f.Machine, "s", client), f.Machine, new(ActorKind.Window, "owner"), "prompt/s/" + client, 0, 0, 1000), p));
    }
}
