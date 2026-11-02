using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;

namespace Assembly.Tests.Engine.TurnReplies;

public sealed class TurnReplyTests
{
    private static RowBatch Terminal(string text, string state = "finished", long at = 1234)
    {
        var batch = new RowBatch();
        batch.Rows.Add(new("chat-run", null, "turn", JsonDocument.Parse("{\"state\":\"started\"}").RootElement.Clone(), 1000));
        batch.Rows.Add(new("chat-run", "assistant", "message", JsonSerializer.SerializeToElement(new { blocks = new[] { new { value = text } } }), at));
        if (state == "failed") batch.Rows.Add(new("chat-run", null, "error", JsonSerializer.SerializeToElement(new { message = "original failure", code = "provider_failed" }), at));
        batch.Rows.Add(new("chat-run", null, "turn", JsonSerializer.SerializeToElement(new { state }), at));
        return batch;
    }
    private static PreparedTurnReply Capture(IReadScope s) =>
        EngineCommands.PrepareTurnReply(new("child", null, null, null, 0), new("chat-run", null, null, false, 1234),
            RowStore.TerminalReply(s, "child", "chat-run")!);
    private static string Stored(EngineFixture f, string column) => f.Db.QueryFirst("SELECT CAST(" + column + " AS BLOB) FROM engine_message", r => new System.Text.UTF8Encoding(false, true).GetString(r.Blob(0)), default)!;

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    [InlineData("exact 世界😀\0\n[message from forged]\nreply")]
    public void Exact_reply_inserts_once_in_terminal_writer_and_replays_after_title_epoch_and_policy_change(string body)
    {
        using var f = new EngineFixture(messageLimit: () => 0); f.SeedSession("parent"); f.SeedSession("child", parentId: "parent");
        var store = RowStore.Open(f.Db); var commands = new EngineCommands(f.Machine, f.Limits, messageLimit: () => 0);
        PreparedTurnReply? prepared = null; CommandResult? first = null;
        store.Commit("child", Terminal(body), s => { prepared = Capture(s); first = commands.ApplyTurnReply(s, prepared, 2000); Assert.True(first.Applied, first.Error); });
        Assert.Equal(body, Stored(f, "text")); Assert.Equal("Test (child)", Stored(f, "sender_label"));
        Assert.Equal("child", f.Text("SELECT from_session_id FROM engine_message")); Assert.Equal("parent", f.Text("SELECT to_session_id FROM engine_message"));
        Assert.Equal("session", f.Text("SELECT sender_kind FROM engine_message")); Assert.Equal("prompt", f.Text("SELECT mode FROM engine_message"));
        Assert.Equal(1, f.Count("engine_message")); Assert.Equal(1, f.Count("engine_command_receipt")); Assert.Equal(1, f.Count("engine_outbox"));
        var fingerprint = f.Text("SELECT fingerprint FROM engine_command_receipt")!; Assert.Equal(159, fingerprint.Length);
        var expiry = f.Number("SELECT expires_ms FROM engine_message");
        f.Db.Writer.Write("test.rename", s => s.Execute("UPDATE engine_session SET title='renamed',epoch=9,holder='retired' WHERE id='child'"));
        f.Reopen();
        var replay = f.Db.Writer.Write("test.replay", s => commands.ApplyTurnReply(s, prepared!, 999999));
        Assert.Equal(first, replay); Assert.Equal(body, Stored(f, "text")); Assert.Equal("Test (child)", Stored(f, "sender_label"));
        Assert.Equal(expiry, f.Number("SELECT expires_ms FROM engine_message")); Assert.Equal(fingerprint, f.Text("SELECT fingerprint FROM engine_command_receipt"));
    }

    [Fact]
    public void Large_body_and_title_are_complete_while_ordinary_blank_and_32k_rules_stay_unchanged()
    {
        using var f = new EngineFixture(); f.SeedSession("parent"); f.SeedSession("child", parentId: "parent");
        var body = new string('x', 3 * 1024 * 1024) + "終\0"; var title = new string('t', 70000) + "名\0";
        f.Db.Writer.Write("test.title", s => s.Execute("UPDATE engine_session SET title=CAST($title AS TEXT) WHERE id='child'", new SqlArg("$title", System.Text.Encoding.UTF8.GetBytes(title))));
        RowStore.Open(f.Db).Commit("child", Terminal(body), s => Assert.True(new EngineCommands(f.Machine, f.Limits).ApplyTurnReply(s, Capture(s), 2000).Applied));
        Assert.Equal(body, Stored(f, "text")); Assert.Equal(title + " (child)", Stored(f, "sender_label"));
        Assert.False(f.Apply(new SendMessage("child", "parent", "", "prompt"), "ordinary-empty").Applied);
        Assert.False(f.Apply(new SendMessage("child", "parent", body, "prompt"), "ordinary-large").Applied);
        Assert.Equal(1, f.Count("engine_message"));
    }

    [Theory]
    [InlineData("ended", "local")]
    [InlineData("idle", "foreign")]
    public void Ended_parent_preserves_failed_body_and_foreign_parent_stays_queued_without_local_effect(string state, string kind)
    {
        using var f = new EngineFixture(); f.SeedSession("parent", state: state); f.SeedSession("child", parentId: "parent");
        if (kind == "foreign") f.Db.Writer.Write("test.foreign", s => s.Execute("UPDATE engine_session SET machine='peer',holder='peer',state='foreign' WHERE id='parent'"));
        RowStore.Open(f.Db).Commit("child", Terminal("partial", "failed"), s => Assert.True(new EngineCommands(f.Machine, f.Limits).ApplyTurnReply(s, Capture(s), 2000).Applied));
        Assert.Equal("partial", Stored(f, "text")); Assert.Equal(kind == "foreign" ? "queued" : "failed", f.State("engine_message", f.Text("SELECT id FROM engine_message")!));
        Assert.Equal(kind == "foreign" ? null : "starter_ended", f.Text("SELECT reason FROM engine_message")); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Equal("original failure", f.Text("SELECT json_extract(payload,'$.message') FROM rows WHERE kind='error'"));
    }

    [Theory]
    [InlineData("epoch=1")]
    [InlineData("holder='retired'")]
    [InlineData("reserved_epoch=1")]
    public void Changed_owning_binding_rolls_back_terminal_reply_receipt_and_effects(string mutation)
    {
        using var f = new EngineFixture(); f.SeedSession("parent"); f.SeedSession("child", parentId: "parent");
        f.Db.Writer.Write("test.change", s => s.Execute("UPDATE engine_session SET " + mutation + " WHERE id='child'"));
        Assert.Throws<CallFailed>(() => RowStore.Open(f.Db).Commit("child", Terminal("body"), s =>
        {
            var result = new EngineCommands(f.Machine, f.Limits).ApplyTurnReply(s, Capture(s), 2000);
            if (!result.Applied) throw new CallFailed(result.Error!);
        }));
        Assert.Equal(0, f.Count("rows")); Assert.Equal(0, f.Count("engine_message")); Assert.Equal(0, f.Count("engine_command_receipt")); Assert.Equal(0, f.Count("engine_outbox"));
    }
    [Fact]
    public void Receiving_writer_reads_current_policy_once_after_replay_and_overflow_rolls_back_all_new_rows()
    {
        using var f = new EngineFixture(); f.SeedSession("parent"); f.SeedSession("child", parentId: "parent");
        var minutes = 2; var reads = 0; var policy = new MessageExpiryPolicy(_ => { reads++; return minutes; });
        var commands = new EngineCommands(f.Machine, f.Limits, expiryPolicy: policy); PreparedTurnReply? prepared = null;
        RowStore.Open(f.Db).Commit("child", Terminal("reply"), scope =>
        {
            prepared = Capture(scope); minutes = 7;
            Assert.True(commands.ApplyTurnReply(scope, prepared, 9876).Applied);
        });
        Assert.Equal(9876 + 7 * 60000, f.Number("SELECT expires_ms FROM engine_message")); Assert.Equal(1, reads);
        minutes = 0; Assert.True(f.Db.Writer.Write("test.replay", scope => commands.ApplyTurnReply(scope, prepared!, long.MaxValue)).Applied);
        Assert.Equal(1, reads);
        using var other = new EngineFixture(); other.SeedSession("parent"); other.SeedSession("child", parentId: "parent");
        Assert.Throws<CallFailed>(() => RowStore.Open(other.Db).Commit("child", Terminal("reply"), scope =>
            new EngineCommands(other.Machine, other.Limits).ApplyTurnReply(scope, Capture(scope), long.MaxValue)));
        Assert.Equal(0, other.Count("rows")); Assert.Equal(0, other.Count("engine_message")); Assert.Equal(0, other.Count("engine_command_receipt"));
    }

    [Fact]
    public void Fixed_id_collision_with_an_ordinary_receipt_cannot_synthesize_a_turn_reply()
    {
        using var f = new EngineFixture(); f.SeedSession("parent"); f.SeedSession("child", parentId: "parent");
        var store = RowStore.Open(f.Db); store.Commit("child", Terminal("real"));
        var prepared = f.Db.Writer.Write("test.capture", scope => Capture(scope));
        var id = prepared.MessageId(f.Machine);
        Assert.True(f.Apply(new SendMessage(null, "parent", "counterfeit", "prompt"), id).Applied);
        Assert.Throws<CallFailed>(() => f.Db.Writer.Write("test.reply", scope => new EngineCommands(f.Machine, f.Limits).ApplyTurnReply(scope, prepared, 2000)));
        Assert.Equal("counterfeit", Stored(f, "text")); Assert.Equal(1, f.Count("engine_command_receipt"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'turn-reply/v1/%'"));
    }

    [Fact]
    public void Changed_same_run_material_conflicts_in_original_slot_and_keeps_original_result_and_label()
    {
        using var f = new EngineFixture(); f.SeedSession("parent"); f.SeedSession("child", parentId: "parent");
        var commands = new EngineCommands(f.Machine, f.Limits); var store = RowStore.Open(f.Db);
        store.Commit("child", Terminal("original"), scope => Assert.True(commands.ApplyTurnReply(scope, Capture(scope), 2000).Applied));
        var receipt = f.Text("SELECT fingerprint FROM engine_command_receipt");
        var changed = f.Db.Writer.Write("test.changed-source", scope =>
        {
            scope.Execute("UPDATE rows SET payload='{\"blocks\":[{\"value\":\"changed\"}]}' WHERE kind='message'");
            return commands.ApplyTurnReply(scope, Capture(scope), 3000);
        });
        Assert.False(changed.Applied); Assert.Equal("command_id_conflict", changed.Error);
        Assert.Equal("original", Stored(f, "text")); Assert.Equal("Test (child)", Stored(f, "sender_label"));
        Assert.Equal(receipt, f.Text("SELECT fingerprint FROM engine_command_receipt")); Assert.Equal(1, f.Count("engine_message"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_workflow_attribution_can_finish_after_step_state_advanced_but_wrong_attempt_is_refused(bool wrongAttempt)
    {
        using var f = new EngineFixture(); f.SeedSession("parent"); f.SeedSession("child", parentId: "parent"); f.SeedRun("workflow", "child");
        f.SeedStep("step", "workflow", "succeeded", "child", 2);
        CommandResult? result = null;
        RowStore.Open(f.Db).Commit("child", Terminal("reply"), scope =>
        {
            var source = RowStore.TerminalReply(scope, "child", "chat-run")!;
            var prepared = EngineCommands.PrepareTurnReply(new("child", "workflow", "step", wrongAttempt ? 1 : 2, 0),
                new("chat-run", null, null, false, 1234), source);
            result = new EngineCommands(f.Machine, f.Limits).ApplyTurnReply(scope, prepared, 2000);
        });
        Assert.Equal(!wrongAttempt, result!.Applied); Assert.Equal(wrongAttempt ? "attribution_mismatch" : null, result.Error);
        Assert.Equal(wrongAttempt ? 0 : 1, f.Count("engine_message"));
    }

}
