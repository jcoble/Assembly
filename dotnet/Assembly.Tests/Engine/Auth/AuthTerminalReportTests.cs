using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthTerminalReportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_failed_terminal_error_and_checked_blocker_commit_in_one_supplied_writer_before_normal_release(bool failedOpen)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(failedOpen);
        var batch = Terminal(f); CommandResult? reported = null;
        var saved = f.Core.Rows.Commit(f.Core.Session.SessionId, batch, scope =>
        {
            var seq = LastTerminal(scope, f.Core.Session.SessionId);
            reported = Report(f, scope, seq); Assert.True(reported.Applied, reported.Error);
            Assert.Equal(1, scope.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'", r => r.Long(0)));
        });
        Assert.NotNull(reported); Assert.Equal("failed", saved.Last(r => r.Kind == "turn").Payload.GetProperty("state").GetString());
        Assert.Equal(f.Proof, saved.Last(r => r.Kind == "turn").Payload.Deserialize(RowJsonContext.Rows.TurnPayload)!.AuthFailure);
        f.AssertReserved(f.Core.Rows.LastSeq(f.Core.Session.SessionId));
        Assert.Equal(1, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM rows WHERE kind='error' AND json_extract(payload,'$.code')='-32000'", r => r.Long(0), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_terminal_receipt_replays_before_current_config_holder_and_epoch_without_another_need_or_negative(bool failedOpen)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(failedOpen);
        var terminal = f.Core.Rows.Commit(f.Core.Session.SessionId, Terminal(f)).Last(r => r.Kind == "turn").Seq;
        var first = f.Core.Db.Writer.Write("test.report-first", scope => Report(f, scope, terminal)); Assert.True(first.Applied, first.Error);
        var version = f.Core.Number("SELECT version FROM engine_provider WHERE id=$id", "claude/");
        f.Configured = false;
        f.Core.Db.Writer.Write("test.move-after-report", scope => scope.Execute("UPDATE engine_session SET holder='remote',epoch=epoch+1 WHERE id=$id", new SqlArg("$id", f.Core.Session.SessionId)));
        Assert.Equal(first, f.Core.Db.Writer.Write("test.report-replay", scope => Report(f, scope, terminal)));
        Assert.Equal(version, f.Core.Number("SELECT version FROM engine_provider WHERE id=$id", "claude/"));
        var changed = f.Core.Db.Writer.Write("test.report-conflict", scope => Report(f, scope, terminal + 1));
        Assert.False(changed.Applied); Assert.Equal("command_id_conflict", changed.Error);
    }

    [Theory]
    [InlineData("wrong-seq")]
    [InlineData("wrong-run")]
    [InlineData("wrong-proof")]
    [InlineData("wrong-state")]
    [InlineData("wrong-error")]
    [InlineData("missing-error")]
    [InlineData("intervening-terminal")]
    [InlineData("wrong-session")]
    [InlineData("copied-client")]
    public async Task Display_rows_or_mismatched_terminal_provenance_cannot_be_promoted_to_an_accepted_auth_terminal(string change)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false);
        var batch = Terminal(f);
        if (change == "missing-error") batch.Rows.RemoveAll(r => r.Kind == "error");
        if (change == "wrong-error")
        {
            var at = batch.Rows.FindIndex(r => r.Kind == "error");
            batch.Rows[at] = batch.Rows[at] with { Payload = JsonSerializer.SerializeToElement(new ErrorPayload("other", "-32603"), RowJsonContext.Rows.ErrorPayload) };
        }
        var index = batch.Rows.FindIndex(r => r.Kind == "turn"); var row = batch.Rows[index];
        batch.Rows[index] = change switch
        {
            "wrong-run" => row with { RunId = "another-execution" },
            "wrong-proof" => row with { Payload = JsonSerializer.SerializeToElement(new TurnPayload("failed", AuthFailure: f.Proof with { ObservationId = Guid.NewGuid() }), RowJsonContext.Rows.TurnPayload) },
            "wrong-state" => row with { Payload = JsonSerializer.SerializeToElement(new TurnPayload("finished", AuthFailure: f.Proof), RowJsonContext.Rows.TurnPayload) },
            _ => row
        };
        if (change == "intervening-terminal") batch.Rows.Insert(index, row with { Payload = JsonSerializer.SerializeToElement(new TurnPayload("finished"), RowJsonContext.Rows.TurnPayload) });
        var saved = f.Core.Rows.Commit(f.Core.Session.SessionId, batch); var seq = saved.Last(r => r.Kind == "turn").Seq;
        var proof = change == "copied-client" ? f.Proof with { ClientId = "other" } : f.Proof;
        var result = f.Core.Db.Writer.Write("test.bad-report", scope => Report(f, scope, change == "wrong-seq" ? seq - 1 : seq, proof,
            change == "wrong-session" ? "another-session" : null));
        Assert.False(result.Applied);
        Assert.Equal(0, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
        Assert.Null(f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.AuthBlocker);
        f.AssertReserved(f.Core.Rows.LastSeq(f.Core.Session.SessionId));
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("command")]
    public async Task Terminal_fact_cannot_borrow_owner_or_another_command_identity(string change)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false);
        var seq = f.Core.Rows.Commit(f.Core.Session.SessionId, Terminal(f)).Last(r => r.Kind == "turn").Seq;
        var context = Context(f);
        context = change == "actor" ? context with { Actor = new(ActorKind.Window, "owner") } : context with { CommandId = Guid.NewGuid() };
        var result = f.Core.Db.Writer.Write("test.forged-report-context", scope => f.Commands.Apply(scope, context, new ReportSignInNeeded(f.Core.Session.SessionId, seq, f.Proof)));
        Assert.False(result.Applied); Assert.Equal(0, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
    }

    [Fact]
    public async Task Failed_blocker_rolls_back_actual_terminal_error_proof_user_binding_and_both_journal_receipts_then_same_batch_retries()
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); var batch = Terminal(f);
        var receipts = f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt", r => r.Long(0), default);
        f.Core.Db.Writer.Write("test.fail-report-need", scope => scope.Execute("CREATE TRIGGER fail_report_need BEFORE INSERT ON engine_needs_me BEGIN SELECT RAISE(ABORT,'need fault'); END"));
        Assert.Throws<SqliteException>(() => f.Core.Rows.Commit(f.Core.Session.SessionId, batch, scope =>
        { var result = Report(f, scope, LastTerminal(scope, f.Core.Session.SessionId)); Assert.True(result.Applied, result.Error); }));
        Assert.Equal(2, f.Core.Rows.LastSeq(f.Core.Session.SessionId)); Assert.Equal(receipts, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt", r => r.Long(0), default));
        Assert.Null(f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.AuthBlocker);
        f.Core.Db.Writer.Write("test.unfault-report-need", scope => scope.Execute("DROP TRIGGER fail_report_need"));
        f.Core.Rows.Commit(f.Core.Session.SessionId, batch, scope => { var result = Report(f, scope, LastTerminal(scope, f.Core.Session.SessionId)); Assert.True(result.Applied, result.Error); });
        Assert.Equal(1, f.Core.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'", r => r.Long(0), default));
    }

    internal static RowBatch Terminal(AuthBlockerTests.Fixture f)
    {
        var batch = f.Converter.Apply(new TurnFailed(1001, "Sign in", "-32000"));
        var at = batch.Rows.FindIndex(r => r.Kind == "turn"); var row = batch.Rows[at];
        batch.Rows[at] = row with { Payload = JsonSerializer.SerializeToElement(new TurnPayload("failed", AuthFailure: f.Proof), RowJsonContext.Rows.TurnPayload) };
        return batch;
    }
    private static long LastTerminal(IReadScope scope, string session) => scope.QueryFirst("SELECT seq FROM rows WHERE session_id=$session AND run_id='R1' AND kind='turn' ORDER BY seq DESC LIMIT 1", r => r.Long(0), new SqlArg("$session", session));
    private static CommandContext Context(AuthBlockerTests.Fixture f) => new(f.Proof.ObservationId, f.Core.Runtime.Machine, new(ActorKind.System, "adapter"),
        "auth-terminal/" + f.Proof.ObservationId.ToString("N"), 0, f.Proof.Epoch, 1001);
    internal static CommandResult Report(AuthBlockerTests.Fixture f, IWriteScope scope, long seq, AuthTerminalProof? proof = null, string? session = null) =>
        f.Commands.Apply(scope, Context(f), new ReportSignInNeeded(session ?? f.Core.Session.SessionId, seq, proof ?? f.Proof));
}
