using System.Text.Json;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Messages;
using Assembly.Application.Features.Engine.Sessions;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine;

public sealed class MessageTests
{
    [Theory]
    [InlineData("delivered_to_turn")]
    [InlineData("next_turn")]
    [InlineData("unsupported")]
    [InlineData("failed")]
    public void Only_adapter_reports_delivery(string outcome)
    {
        using var f = new EngineFixture(); f.SeedSession("b");
        Assert.True(f.Apply(new SendMessage(null, "b", "hello", "prompt"), "m").Applied);
        Assert.Equal("queued", f.State("engine_message", "m"));
        var r = f.Apply(new MessageOutcome(outcome, "turn-1", null), "m", version: 1,
            actor: new(ActorKind.System, "adapter"));
        Assert.True(r.Applied); Assert.Equal(outcome, f.State("engine_message", "m"));
        Assert.Equal("turn-1", f.Text("SELECT provider_turn_id FROM engine_message"));
        Assert.Equal("invalid_transition", f.Apply(new MessageOutcome("failed", null, "changed"), "m", version: 2,
            actor: new(ActorKind.System, "adapter")).Error);
        Assert.Equal(outcome, f.State("engine_message", "m"));
    }

    [Theory]
    [InlineData("codex", "claude")]
    [InlineData("claude", "codex")]
    public void Child_end_notifies_once_after_restart(string childProvider, string rootProvider)
    {
        using var f = new EngineFixture(); f.SeedSession("root", provider: rootProvider);
        f.SeedSession("child", parentId: "root", provider: childProvider);
        var id = Guid.NewGuid(); var end = new SetSessionState("ended", null, null);
        var result = f.Apply(end, "child", actor: new(ActorKind.System, "adapter"), commandId: id);
        Assert.True(result.Applied); f.Reopen();
        Assert.Equal(result, f.Apply(end, "child", actor: new(ActorKind.System, "adapter"), commandId: id));
        Assert.Equal(1, f.Count("engine_message"));
        Assert.Equal("queued", f.State("engine_message", "child-ended/child"));
        Assert.Equal("root", f.Text("SELECT to_session_id FROM engine_message"));
        Assert.Equal("system", f.Text("SELECT sender_kind FROM engine_message"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
        Assert.Equal("send/child-ended/child", f.Text("SELECT id FROM engine_outbox"));
        Assert.DoesNotContain("text", JsonSerializer.Serialize(f.Events(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    [Fact]
    public void No_starter_skips_notification_and_ended_starter_records_failure()
    {
        using var f = new EngineFixture(); f.SeedSession("root", "ended");
        f.SeedSession("child", parentId: "root"); f.SeedSession("alone");
        foreach (var child in new[] { "child", "alone" })
            Assert.True(f.Apply(new SetSessionState("ended", null, null), child,
                actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal(1, f.Count("engine_message"));
        Assert.Equal("failed", f.State("engine_message", "child-ended/child"));
        Assert.Equal("starter_ended", f.Text("SELECT reason FROM engine_message"));
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Theory]
    [InlineData("codex", "claude")]
    [InlineData("claude", "codex")]
    public void Interrupt_requires_stop_ack(string senderProvider, string recipientProvider)
    {
        using var f = new EngineFixture(); f.SeedSession("a", provider: senderProvider);
        f.SeedSession("b", "running", provider: recipientProvider);
        Assert.True(f.Apply(new SendMessage("a", "b", "private text", "prompt"), "prompt",
            actor: new(ActorKind.Session, "a")).Applied);
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='stop'"));
        var interrupt = new SendMessage(null, "b", "interrupt", "interrupt"); var id = Guid.NewGuid();
        Assert.True(f.Apply(interrupt, "m", commandId: id).Applied);
        f.Reopen(); Assert.True(f.Apply(interrupt, "m", commandId: id).Applied);
        Assert.True(f.Apply(interrupt, "m2").Applied);
        Assert.Equal("held", f.State("engine_message", "m"));
        Assert.Equal("held", f.State("engine_message", "m2"));
        Assert.Equal("message-stop/m", f.Text("SELECT id FROM engine_outbox WHERE kind='stop'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='stop'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
        Assert.Equal(1, f.Number("SELECT cancel_requested FROM engine_session WHERE id='b'"));
        Assert.Equal("unavailable", f.Apply(new MessageOutcome("delivered_to_turn", "t", null), "m", version: 1,
            actor: new(ActorKind.System, "adapter")).Error);
        Assert.DoesNotContain("private text", f.Text("SELECT group_concat(data_json) FROM engine_event")!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Session_end_and_stop_ack_roll_back_with_message_effect(bool acknowledgeStop)
    {
        using var f = new EngineFixture(); f.SeedSession("root"); f.SeedSession("child", parentId: "root");
        var version = 0L;
        if (acknowledgeStop)
        {
            Assert.True(f.Apply(new SendMessage(null, "child", "hello", "interrupt"), "m").Applied);
            version = 1;
        }
        var events = f.Count("engine_event"); var effects = f.Count("engine_outbox");
        var receipts = f.Count("engine_command_receipt"); var next = f.Number("SELECT next_seq FROM engine_meta");
        f.Db.Writer.Write("test.fail-send", s => s.Execute(
            "CREATE TRIGGER fail_send BEFORE INSERT ON engine_outbox WHEN NEW.kind='send' BEGIN SELECT RAISE(ABORT,'injected'); END"));
        Assert.Throws<SqliteException>(() => f.Apply(new SetSessionState(acknowledgeStop ? "idle" : "ended", null, null),
            "child", version: version, actor: new(ActorKind.System, "adapter")));
        Assert.Equal("idle", f.State("engine_session", "child"));
        Assert.Equal(version, f.Number("SELECT version FROM engine_session WHERE id='child'"));
        Assert.Equal(acknowledgeStop ? "held" : null, f.State("engine_message", acknowledgeStop ? "m" : "child-ended/child"));
        Assert.Equal(acknowledgeStop ? 1 : 0, f.Number("SELECT cancel_requested FROM engine_session WHERE id='child'"));
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(effects, f.Count("engine_outbox"));
        Assert.Equal(receipts, f.Count("engine_command_receipt")); Assert.Equal(next, f.Number("SELECT next_seq FROM engine_meta"));
    }

    [Fact]
    public void Sender_and_terminal_outcome_are_checked()
    {
        using var f = new EngineFixture(); f.SeedSession("a"); f.SeedSession("b");
        Assert.Equal("forbidden", f.Apply(new SendMessage("a", "b", "hi", "prompt"), "forged").Error);
        Assert.Equal("forbidden", f.Apply(new SendMessage("b", "a", "hi", "prompt"), "forged2",
            actor: new(ActorKind.Session, "a")).Error);
        Assert.Equal("forbidden", f.Apply(new SendMessage(null, "b", "hi", "prompt"), "system",
            actor: new(ActorKind.System, "adapter")).Error);
        Assert.Equal("bad_args", f.Apply(new SendMessage(null, "missing", "hi", "prompt"), "missing").Error);
        Assert.Equal("bad_args", f.Apply(new SendMessage(null, "b", "hi", "other"), "mode").Error);
        Assert.True(f.Apply(new SendMessage(null, "b", "hi", "prompt"), "m").Applied);
        Assert.Equal("window", f.Text("SELECT sender_kind FROM engine_message"));
        var payload = JsonSerializer.Deserialize(f.Text("SELECT payload_json FROM engine_outbox")!,
            global::Assembly.Application.Features.Engine.EngineJson.Default.SendMessage);
        Assert.Equal(new SendMessage(null, "b", "hi", "prompt"), payload);
        Assert.Null(f.Text("SELECT from_session_id FROM engine_message"));
        Assert.Equal("forbidden", f.Apply(new MessageOutcome("failed", null, null), "m", version: 1).Error);
        Assert.Equal("version_conflict", f.Apply(new MessageOutcome("failed", null, null), "m",
            actor: new(ActorKind.System, "adapter")).Error);
        Assert.Equal("bad_args", f.Apply(new MessageOutcome("other", null, null), "m", version: 1,
            actor: new(ActorKind.System, "adapter")).Error);
        Assert.Equal(1, f.Count("engine_message"));
    }
}
