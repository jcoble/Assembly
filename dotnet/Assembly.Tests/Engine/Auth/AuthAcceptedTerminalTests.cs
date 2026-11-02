using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthAcceptedTerminalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_read_uses_the_exact_accepted_report_and_retained_terminal_even_after_current_owner_changes(bool failedOpen)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(failedOpen);
        var seq = Commit(f); Assert.True(Read(f, seq));
        using (var scope = f.Core.Db.OpenSnapshot(default))
        {
            var indexed = scope.Query("""
                SELECT seq FROM rows INDEXED BY rows_auth_terminal
                WHERE session_id=$session AND run_id=$run AND json_extract(payload,'$.authFailure.delivery.effectId')=$effect
                  AND kind='turn' AND json_type(payload,'$.authFailure')='object' ORDER BY seq LIMIT 2
                """, r => r.Long(0), new SqlArg("$session", f.Core.Session.SessionId), new("$run", f.Proof.ChatRunId), new("$effect", f.Effect.Id));
            Assert.Equal([seq], indexed); Assert.Equal(5, f.Core.Rows.StoredFormatVersion);
        }
        f.Configured = false;
        f.Core.Db.Writer.Write("test.accepted-later-owner", scope => scope.Execute("UPDATE engine_session SET epoch=epoch+1,holder='remote' WHERE id=$id", new SqlArg("$id", f.Core.Session.SessionId)));
        Assert.True(Read(f, seq)); Assert.False(Read(f, seq + 1));
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("error")]
    [InlineData("fingerprint")]
    [InlineData("result")]
    [InlineData("missing")]
    [InlineData("proof")]
    public async Task Missing_changed_or_refused_retained_evidence_is_never_an_accepted_terminal(string change)
    {
        await using var f = await AuthBlockerTests.Fixture.Create(false); var seq = Commit(f); Assert.True(Read(f, seq));
        f.Core.Db.Writer.Write("test.damage-accepted-evidence", scope => scope.Execute(change switch
        {
            "terminal" => "UPDATE rows SET payload=json_set(payload,'$.authFailure.clientId','another-client') WHERE session_id=$session AND seq=$seq",
            "error" => "UPDATE rows SET payload=json_set(payload,'$.code','-32603') WHERE session_id=$session AND kind='error'",
            "fingerprint" => "UPDATE engine_command_receipt SET fingerprint='changed' WHERE command_id=$command",
            "result" => "UPDATE engine_command_receipt SET result_json=json_set(result_json,'$.applied',json('false')) WHERE command_id=$command",
            "missing" => "DELETE FROM engine_command_receipt WHERE command_id=$command",
            _ => "SELECT 1"
        }, new SqlArg("$session", f.Core.Session.SessionId), new("$seq", seq), new("$command", f.Proof.ObservationId.ToString("N"))));
        Assert.False(Read(f, seq, change == "proof" ? f.Proof with { ProviderMessageId = "copied-insertion" } : null));
        Assert.Equal("queued", f.Core.State("engine_message", f.Proof.Delivery!.MessageId));
        Assert.Equal("inflight", f.Core.State("engine_outbox", f.Effect.Id));
        Assert.Null(f.Core.Rows.UserDeliveryOfClient(f.Core.Session.SessionId, f.Proof.ClientId!)!.Acknowledgment);
    }

    private static long Commit(AuthBlockerTests.Fixture f)
    {
        var before = f.Core.Rows.LastSeq(f.Core.Session.SessionId);
        var batch = AuthTerminalReportTests.Terminal(f); var seq = before + batch.Rows.FindIndex(r => r.Kind == "turn") + 1;
        f.Core.Rows.Commit(f.Core.Session.SessionId, batch, scope =>
        { var result = AuthTerminalReportTests.Report(f, scope, seq); Assert.True(result.Applied, result.Error); });
        return seq;
    }
    private static bool Read(AuthBlockerTests.Fixture f, long seq, AuthTerminalProof? proof = null)
    { using var scope = f.Core.Db.OpenSnapshot(default); return f.Commands.IsAcceptedAuthTerminal(scope, f.Core.Session.SessionId, seq, proof ?? f.Proof); }
}
