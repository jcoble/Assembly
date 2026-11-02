using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Engine.Delivery;

public sealed class AuthRetryRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_retry_joins_its_own_execution_and_acks_the_original_user_once(bool helper)
    {
        await using var f = new AdapterDeliveryFixture(configured: (p, a) => p == "claude" && a == "");
        var sessionId = helper ? f.Sessions.Helper(f.Session, "claude", "Actual helper").SessionId : f.Session.SessionId;
        var (original, effect) = await AuthRetryStartTests.RefusedAndSignedIn(f, sessionId);
        var sending = f.Effects.ExecuteAsync(effect, default);
        var agent = await f.Launcher.NextAsync();
        try
        {
            await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
            var run = f.Hosts.For(sessionId).CurrentTurn.RunId!;
            Assert.NotEqual(original.RunId, run);
            Assert.Equal("exact body", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
            var joined = f.Effects.ExecuteAsync(effect, default);
            Assert.False(sending.IsCompleted); Assert.False(joined.IsCompleted);
            await agent.InsertAsync(prompt, "actual-retry-insertion");
            var receipt = await sending.WaitAsync(TimeSpan.FromSeconds(8));
            var joinedReceipt = await joined.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(receipt.Disposition, joinedReceipt.Disposition); Assert.Equal(receipt.Data.GetRawText(), joinedReceipt.Data.GetRawText());
            var data = receipt.Data.Deserialize(EngineJson.Default.MessageEffectData)!;
            Assert.Equal(run, data.RetryExecution!.ChatRunId);
            Assert.Equal(original.RunId, data.UserRow!.RunId); Assert.Equal(original.Seq, data.UserRow.Seq);
            Assert.True(f.Complete(effect, receipt).Applied);
            await agent.TextAsync("actual second execution answer");
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
            await agent.CloseAsync(); await f.Hosts.For(sessionId).CurrentTurn.Done.WaitAsync(TimeSpan.FromSeconds(8));
            using (var scope = f.Db.OpenSnapshot(default))
            {
                var user = f.Rows.UserDeliveryOfClient(scope, sessionId, "C")!;
                Assert.Equal(original.RunId, user.RunId); Assert.Equal(original.Seq, user.Seq);
                Assert.Equal(run, user.RetryExecution!.ChatRunId);
                Assert.Equal("actual-retry-insertion", user.Acknowledgment!.ProviderMessageId);
            }
            Assert.Single(f.Rows.RowsAfter(sessionId, 0), row => row.Kind == "user");
            Assert.Equal(2, f.Db.QueryFirst("SELECT COUNT(*) FROM rows WHERE kind='turn' AND json_extract(payload,'$.state')='started'", r => r.Long(0), default));
            if (helper)
            {
                var replies = f.Db.Query("SELECT id,text FROM engine_message WHERE from_session_id=$session AND id LIKE 'turn-reply/%'",
                    r => (Id: r.Text(0), Text: r.Text(1)), default, new SqlArg("$session", sessionId));
                Assert.Equal(2, replies.Count); Assert.Equal(2, replies.Select(r => r.Id).Distinct().Count());
                Assert.Contains(replies, r => r.Text == ""); Assert.Contains(replies, r => r.Text == "actual second execution answer");
            }
            var duplicate = Assert.IsType<SendResult.Inserted>(await f.Hosts.For(sessionId).SendAsync("exact body", "C", null, default));
            Assert.Equal(run, duplicate.RunId);
            var reconciled = await f.Effects.ReconcileAsync(effect, default);
            Assert.Equal(data.RetryExecution, reconciled.Data.Deserialize(EngineJson.Default.MessageEffectData)!.RetryExecution);
            Assert.Equal(2, f.Launcher.Launches);
        }
        finally { agent.Crash(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_auth_terminal_uses_real_execution_and_retains_original_user_identity(bool failedOpen)
    {
        await using var f = new AdapterDeliveryFixture(configured: (p, a) => p == "claude" && a == "");
        var (original, effect) = await AuthRetryStartTests.RefusedAndSignedIn(f);
        var sending = f.Effects.ExecuteAsync(effect, default);
        var agent = await f.Launcher.NextAsync();
        try
        {
            var request = await agent.ReadAsync("initialize");
            if (!failedOpen)
            {
                await agent.ReplyAsync(request, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
                await agent.OpenAsync(); request = await agent.ReadAsync("session/prompt");
            }
            var (run, done) = f.Hosts.For(f.Session.SessionId).CurrentTurn;
            await agent.ReplyErrorAsync(request, -32000, "Sign in again");
            var receipt = await sending.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
            Assert.True(f.Complete(effect, receipt).Applied);
            if (!failedOpen) await agent.CloseAsync();
            await done.WaitAsync(TimeSpan.FromSeconds(8));
            var terminal = Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.RunId == run && row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "failed");
            var proof = terminal.Payload.Deserialize(RowJsonContext.Rows.TurnPayload)!.AuthFailure!;
            Assert.Equal(failedOpen ? 2 : 1, proof.Version); Assert.Equal(run, proof.ChatRunId);
            Assert.NotEqual(original.RunId, proof.ChatRunId); Assert.Equal(original.RunId, proof.Delivery!.UserRunId);
            Assert.Equal(original.Seq, proof.Delivery.UserRowSeq); Assert.Equal(effect.Id, proof.Delivery.EffectId);
            using var scope = f.Db.OpenSnapshot(default);
            Assert.True(EngineCommands.IsAcceptedAuthTerminal(scope, f.Session.SessionId, terminal.Seq, proof));
            Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'", r => r.Long(0), default));
            Assert.Single(f.Rows.RowsAfter(f.Session.SessionId, 0), row => row.Kind == "user");
        }
        finally { agent.Crash(); }
    }
}
