using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Effects;

namespace Assembly.Tests.Engine.Delivery;

public sealed class AuthRetryOrderingTests
{
    [Fact]
    public async Task Retry_keeps_original_expiry_without_opening_another_auth_need()
    {
        await using var f = new AdapterDeliveryFixture(configured: (p, a) => p == "claude" && a == "");
        var (_, effect) = await AuthRetryStartTests.RefusedAndSignedIn(f);
        var executing = f.Effects.ExecuteAsync(effect, default); var agent = await f.Launcher.NextAsync();
        try
        {
            var initialize = await agent.ReadAsync("initialize"); var done = f.Hosts.For(f.Session.SessionId).CurrentTurn.Done;
            f.Db.Writer.Write("test.retry-expiry", scope => scope.Execute("UPDATE engine_message SET expires_ms=$now WHERE id=$id",
                new SqlArg("$now", f.Time.GetUtcNow().ToUnixTimeMilliseconds()), new("$id", effect.Payload.GetProperty("messageId").GetString())));
            await agent.ReplyErrorAsync(initialize, -32000, "Sign in");
            var receipt = await executing.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
            Assert.True(f.Complete(effect, receipt).Applied); await done.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'", r => r.Long(0), default));
            Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", r => r.Long(0), default));
            Assert.Equal("expired", f.Db.QueryFirst("SELECT reason FROM engine_message WHERE id=$id", r => r.Text(0), default,
                new SqlArg("$id", effect.Payload.GetProperty("messageId").GetString())));
            Assert.Equal("signed_in", f.Db.QueryFirst("SELECT auth_state FROM engine_provider WHERE id='claude/'", r => r.Text(0), default));
        }
        finally { agent.Crash(); }
    }

    [Fact]
    public async Task Retry_waits_for_exact_prior_close_instead_of_joining_its_completed_auth_result()
    {
        await using var f = new AdapterDeliveryFixture(configured: (p, a) => p == "claude" && a == "");
        var message = f.Queue(); var first = f.Claim(message);
        var firstExecution = f.Effects.ExecuteAsync(first, default); var agent = await f.Launcher.NextAsync();
        try
        {
            await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
            var (firstRun, priorDone) = f.Hosts.For(f.Session.SessionId).CurrentTurn;
            await agent.ReplyErrorAsync(prompt, -32000, "Sign in");
            var refusal = await firstExecution.WaitAsync(TimeSpan.FromSeconds(8));
            var close = await agent.ReadAsync("session/close"); Assert.False(priorDone.IsCompleted);
            Assert.True(f.Complete(first, refusal).Applied); AuthRetryStartTests.SignIn(f);
            var retry = f.Claim(message); var retryExecution = f.Effects.ExecuteAsync(retry, default);
            Assert.False(retryExecution.IsCompleted); Assert.Equal(1, f.Launcher.Launches);
            Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM rows WHERE kind='turn' AND json_extract(payload,'$.state')='started'", r => r.Long(0), default));
            await agent.ReplyAsync(close, "{}"); await priorDone.WaitAsync(TimeSpan.FromSeconds(8));
            {
                await agent.OpenAsync(); var retryPrompt = await agent.ReadAsync("session/prompt");
                var (secondRun, done) = f.Hosts.For(f.Session.SessionId).CurrentTurn;
                Assert.NotEqual(firstRun, secondRun);
                await agent.InsertAsync(retryPrompt, "after-actual-close");
                var receipt = await retryExecution.WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Equal("next_turn", receipt.Data.Deserialize(EngineJson.Default.MessageEffectData)!.State);
                Assert.True(f.Complete(retry, receipt).Applied);
                await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
                await agent.CloseAsync(); await done.WaitAsync(TimeSpan.FromSeconds(8));
            }
        }
        finally { agent.Crash(); }
    }

    [Fact]
    public async Task Actual_retry_start_fault_leaves_no_execution_mapping_or_provider_launch_then_valid_retry_works()
    {
        await using var f = new AdapterDeliveryFixture(configured: (p, a) => p == "claude" && a == "");
        var (user, effect) = await AuthRetryStartTests.RefusedAndSignedIn(f);
        var before = f.Rows.LastSeq(f.Session.SessionId);
        f.Db.Writer.Write("test.fail-retry-user-binding", scope => scope.Execute("""
            CREATE TRIGGER fail_retry_user_binding BEFORE UPDATE OF payload ON rows
            WHEN NEW.kind='user' AND json_type(NEW.payload,'$.retryExecution')='object'
            BEGIN SELECT RAISE(ABORT,'retry binding fault'); END
            """));
        var refused = await f.Effects.ExecuteAsync(effect, default);
        Assert.Equal(EffectDisposition.Unknown, refused.Disposition);
        Assert.Equal(before, f.Rows.LastSeq(f.Session.SessionId)); Assert.Equal(1, f.Launcher.Launches);
        Assert.Null(f.Hosts.For(f.Session.SessionId).CurrentTurn.RunId);
        using (var scope = f.Db.OpenSnapshot(default))
            Assert.Null(f.Rows.UserDeliveryOfClient(scope, f.Session.SessionId, "C")!.RetryExecution);
        f.Db.Writer.Write("test.unfail-retry-user-binding", scope => scope.Execute("DROP TRIGGER fail_retry_user_binding"));
        var executing = f.Effects.ExecuteAsync(effect, default); var agent = await f.Launcher.NextAsync();
        try
        {
            await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
            var (run, done) = f.Hosts.For(f.Session.SessionId).CurrentTurn; Assert.NotEqual(user.RunId, run);
            await agent.InsertAsync(prompt, "one-valid-retry");
            Assert.True(f.Complete(effect, await executing.WaitAsync(TimeSpan.FromSeconds(8))).Applied);
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
            await agent.CloseAsync(); await done.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(2, f.Launcher.Launches);
        }
        finally { agent.Crash(); }
    }
}
