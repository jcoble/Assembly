using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth.Retry;

public sealed class AuthPriorTerminalTests
{
    [Theory]
    [InlineData("finished")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void Top_level_actual_terminal_is_a_boundary_without_a_helper_reply(string state)
    {
        using var f = new EngineFixture(); f.SeedSession("child"); RowStore.Open(f.Db).Commit("child", Batch("body", state));
        Assert.True(Read(f)); Assert.Equal(0, f.Count("engine_message"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    [InlineData("exact 世界😀\0\nreply")]
    public void Helper_requires_its_actual_applied_C6_with_exact_empty_or_full_body_and_survives_reopen(string text)
    {
        using var f = Helper(); CommitReply(f, text); var receipt = f.Text("SELECT fingerprint FROM engine_command_receipt");
        Assert.True(Read(f)); f.Reopen(); Assert.True(Read(f)); Assert.Equal(receipt, f.Text("SELECT fingerprint FROM engine_command_receipt"));
        Assert.Equal(1, f.Count("engine_message")); Assert.Equal(1, f.Count("engine_command_receipt"));
    }

    [Fact]
    public void Large_C6_body_is_compared_without_truncation_or_label_recapture()
    {
        using var f = Helper(); CommitReply(f, new string('x', 3 * 1024 * 1024) + "終\0");
        f.Db.Writer.Write("test.rename-prior", s => s.Execute("UPDATE engine_session SET title='later title' WHERE id='child'"));
        Assert.True(Read(f));
    }

    [Theory]
    [InlineData("missing-receipt")]
    [InlineData("negative")]
    [InlineData("carrier")]
    [InlineData("missing-message")]
    [InlineData("body")]
    [InlineData("source")]
    [InlineData("terminal")]
    [InlineData("machine")]
    [InlineData("wrong-child")]
    [InlineData("wrong-run")]
    public void Missing_copied_or_conflicting_helper_boundary_never_allows_a_retry(string change)
    {
        using var f = Helper(); CommitReply(f, "original\0body");
        if (change is not ("wrong-child" or "wrong-run")) f.Db.Writer.Write("test.corrupt-prior-boundary", s => s.Execute(change switch
        {
            "missing-receipt" => "DELETE FROM engine_command_receipt",
            "negative" => "UPDATE engine_command_receipt SET result_json=json_set(result_json,'$.applied',json('false'))",
            "carrier" => "UPDATE engine_command_receipt SET fingerprint=substr(fingerprint,1,length(fingerprint)-1)||CASE substr(fingerprint,-1) WHEN 'A' THEN 'B' ELSE 'A' END",
            "missing-message" => "DELETE FROM engine_message",
            "body" => "UPDATE engine_message SET text='changed'",
            "source" => "UPDATE rows SET payload=json_set(payload,'$.blocks[0].value','changed') WHERE kind='message'",
            "terminal" => "UPDATE rows SET payload=json_set(payload,'$.state','started') WHERE kind='turn' AND json_extract(payload,'$.state')='failed'",
            _ => "UPDATE engine_meta SET machine_id='wrong-database-authority' WHERE singleton=1"
        }));
        Assert.False(Read(f, change == "wrong-child" ? "other" : "child", change == "wrong-run" ? "other" : "run"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_started_run_or_helper_terminal_without_C6_is_insufficient(bool terminal)
    {
        using var f = Helper(); var batch = Batch("body"); if (!terminal) batch.Rows.RemoveRange(1, batch.Rows.Count - 1); RowStore.Open(f.Db).Commit("child", batch);
        Assert.False(Read(f));
    }

    [Fact]
    public void Source_terminal_and_C6_rollback_leave_no_boundary_then_same_batch_retries()
    {
        using var f = Helper(); var batch = Batch(""); f.Db.Writer.Write("test.prior-c6-fault", s => s.Execute("CREATE TRIGGER fail_prior_c6 BEFORE INSERT ON engine_message BEGIN SELECT RAISE(ABORT,'prior C6 fault'); END"));
        Assert.Throws<SqliteException>(() => CommitReply(f, "", batch)); Assert.False(Read(f)); Assert.Equal(0, f.Count("rows")); Assert.Equal(0, f.Count("engine_command_receipt"));
        f.Db.Writer.Write("test.prior-c6-unfault", s => s.Execute("DROP TRIGGER fail_prior_c6")); CommitReply(f, "", batch); Assert.True(Read(f));
    }

    [Fact]
    public void Genuine_RunRecovery_terminal_and_existing_recovery_C6_satisfy_the_same_boundary()
    {
        using var f = new EngineFixture(); var sessions = SessionStore.Open(f.Db, f.Time, "checkouts"); var parent = sessions.Create("claude", "https://github.com/o/r"); var child = sessions.Helper(parent, "claude", "Child");
        f.SeedSession(parent.SessionId); f.SeedSession(child.SessionId, parentId: parent.SessionId);
        var store = RowStore.Open(f.Db); using var hub = new LiveHub(store); var batch = new RowBatch(); batch.Rows.Add(new("run", null, "turn", JsonSerializer.SerializeToElement(new { state = "started" }), 100)); store.Commit(child.SessionId, batch);
        Assert.False(Read(f, child.SessionId)); var commands = new EngineCommands(f.Machine, f.Limits);
        Assert.Equal(1, RunRecovery.CloseUnfinished(store, hub, 222, _ => true, (s, recovered) =>
        { var source = RowStore.TerminalReply(s, child.SessionId, "run")!; var reply = EngineCommands.PrepareRecoveredTurnReply(recovered, source, f.Machine, 0); Assert.True(commands.ApplyRecoveredTurnReply(s, reply, 222).Applied); }));
        Assert.True(Read(f, child.SessionId)); Assert.Equal(0, RunRecovery.CloseUnfinished(store, hub, 333, _ => true, (_, _) => throw new InvalidOperationException("duplicate")));
    }

    private static EngineFixture Helper() { var f = new EngineFixture(); f.SeedSession("parent"); f.SeedSession("child", parentId: "parent"); return f; }
    private static bool Read(EngineFixture f, string child = "child", string run = "run") => f.Db.Writer.Write("test.prior-auth-terminal", s => EngineCommands.HasPriorAuthTerminal(s, child, run));
    private static void CommitReply(EngineFixture f, string text, RowBatch? batch = null) => RowStore.Open(f.Db).Commit("child", batch ?? Batch(text), s =>
    { var source = RowStore.TerminalReply(s, "child", "run")!; var reply = EngineCommands.PrepareTurnReply(new("child", null, null, null, 0), new("run", null, null, false, 222), source); Assert.True(new EngineCommands(f.Machine, f.Limits).ApplyTurnReply(s, reply, 222).Applied); });
    private static RowBatch Batch(string text, string state = "failed")
    {
        var batch = new RowBatch(); batch.Rows.Add(new("run", null, "turn", JsonSerializer.SerializeToElement(new { state = "started" }), 100));
        batch.Rows.Add(new("run", "assistant", "message", JsonSerializer.SerializeToElement(new { blocks = new[] { new { value = text } } }), 222));
        if (state == "failed") batch.Rows.Add(new("run", null, "error", JsonSerializer.SerializeToElement(new { message = "actual failure", code = "-32000" }), 222));
        batch.Rows.Add(new("run", null, "turn", JsonSerializer.SerializeToElement(new { state }), 222)); return batch;
    }
}
