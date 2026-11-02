using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.Providers;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Delivery;

public sealed class AuthRetryStartTests
{
    [Fact]
    public async Task Actual_refusal_and_login_allow_one_distinct_started_execution_bound_to_original_user()
    {
        await using var f = new AdapterDeliveryFixture(configured: (p, a) => p == "claude" && a == "");
        var (original, retry) = await RefusedAndSignedIn(f);
        var before = UserBytes(f);
        var execution = NewExecution(original, retry);
        var result = Start(f, execution);
        Assert.True(result.Applied, result.Error);
        using (var scope = f.Db.OpenSnapshot(default))
        {
            var user = f.Rows.UserDeliveryOfClient(scope, f.Session.SessionId, "C")!;
            Assert.Equal(original.RunId, user.RunId); Assert.Equal(original.Seq, user.Seq);
            Assert.Equal(execution, user.RetryExecution); Assert.Null(user.Acknowledgment);
            Assert.True(EngineCommands.IsAcceptedAuthRetryStarted(scope, f.Session.SessionId, execution));
            Assert.False(EngineCommands.IsAcceptedAuthRetryStarted(scope, f.Session.SessionId, execution with { PriorEffectId = "copied" }));
        }
        Assert.NotEqual(before, UserBytes(f));
        Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM rows WHERE kind='user'", r => r.Long(0), default));
        var rows = f.Rows.LastSeq(f.Session.SessionId);
        Assert.Equal(result, Apply(f, execution)); Assert.Equal(rows, f.Rows.LastSeq(f.Session.SessionId));
        var replacement = execution with { ChatRunId = Guid.NewGuid().ToString() };
        Assert.False(Start(f, replacement).Applied);
    }

    [Fact]
    public async Task Start_receipt_fault_rolls_back_started_row_and_original_user_mapping()
    {
        await using var f = new AdapterDeliveryFixture(configured: (p, a) => p == "claude" && a == "");
        var (original, retry) = await RefusedAndSignedIn(f);
        var execution = NewExecution(original, retry); var before = UserBytes(f); var rows = f.Rows.LastSeq(f.Session.SessionId);
        f.Db.Writer.Write("test.fail-retry-start", scope => scope.Execute("""
            CREATE TRIGGER fail_retry_start BEFORE INSERT ON engine_command_receipt
            WHEN NEW.command_id=$unused BEGIN SELECT RAISE(ABORT,'retry start fault'); END
            """.Replace("$unused", "'" + Guid.Parse(execution.ChatRunId).ToString("N") + "'")));
        Assert.Throws<SqliteException>(() => Start(f, execution));
        Assert.Equal(before, UserBytes(f)); Assert.Equal(rows, f.Rows.LastSeq(f.Session.SessionId));
        f.Db.Writer.Write("test.unfail-retry-start", scope => scope.Execute("DROP TRIGGER fail_retry_start"));
        Assert.True(Start(f, execution).Applied);
    }

    [Theory]
    [InlineData("effect")]
    [InlineData("prior")]
    [InlineData("claim")]
    [InlineData("user")]
    [InlineData("run")]
    [InlineData("epoch")]
    [InlineData("expired")]
    public async Task A_changed_retry_binding_cannot_allocate_an_execution(string change)
    {
        await using var f = new AdapterDeliveryFixture(configured: (p, a) => p == "claude" && a == "");
        var (original, retry) = await RefusedAndSignedIn(f); var execution = NewExecution(original, retry);
        execution = change switch
        {
            "effect" => execution with { EffectId = "unclaimed" },
            "prior" => execution with { PriorEffectId = "wrong-prior" },
            "claim" => execution with { ClaimAttempt = execution.ClaimAttempt + 1 },
            "user" => execution with { UserRowSeq = original.Seq + 1 },
            "run" => execution with { ChatRunId = original.RunId! },
            "epoch" => execution with { Epoch = execution.Epoch + 1 },
            _ => execution
        };
        if (change == "expired") f.Db.Writer.Write("test.expired-retry-start", scope => scope.Execute("UPDATE engine_message SET expires_ms=1000 WHERE id=$id", new SqlArg("$id", execution.MessageId)));
        var before = UserBytes(f); Assert.False(Start(f, execution).Applied); Assert.Equal(before, UserBytes(f));
    }

    private static AuthRetryExecution NewExecution(UserDeliveryRow user, Effect retry) => new(Guid.NewGuid().ToString(),
        retry.Payload.GetProperty("messageId").GetString()!, retry.Id, retry.Attempt, user.AuthBlocker!.EffectId,
        "C", 0, user.Seq, user.RunId!);

    private static CommandResult Start(AdapterDeliveryFixture f, AuthRetryExecution execution)
    {
        var batch = new RowBatch(); batch.Rows.Add(new(execution.ChatRunId, null, "turn",
            JsonSerializer.SerializeToElement(new TurnPayload("started"), RowJsonContext.Rows.TurnPayload), 1000));
        CommandResult result = null!;
        try
        {
            f.Rows.Commit(f.Session.SessionId, batch, scope =>
            {
                result = Apply(f, execution, scope);
                if (!result.Applied) throw new CallFailed(result.Error ?? "retry start refused");
            });
        }
        catch (CallFailed) when (result is { Applied: false }) { }
        return result;
    }

    private static CommandResult Apply(AdapterDeliveryFixture f, AuthRetryExecution execution, IWriteScope? scope = null)
    {
        var context = new CommandContext(Guid.Parse(execution.ChatRunId), f.Runtime.Machine, new(ActorKind.System, "adapter"),
            "auth-retry/" + execution.ChatRunId, 0, execution.Epoch, 1000);
        var command = new RecordAuthRetryStarted(f.Session.SessionId, execution);
        return scope is null ? f.Db.Writer.Write("test.retry-start", s => f.Commands.Apply(s, context, command)) : f.Commands.Apply(scope, context, command);
    }

    private static string? UserBytes(AdapterDeliveryFixture f) => f.Db.QueryFirst("SELECT payload FROM rows WHERE session_id=$id AND kind='user'",
        r => r.Text(0), default, new SqlArg("$id", f.Session.SessionId));

    internal static async Task<(UserDeliveryRow User, Effect Retry)> RefusedAndSignedIn(AdapterDeliveryFixture f, string? sessionId = null)
    {
        sessionId ??= f.Session.SessionId;
        var starts = f.Db.Query("SELECT id FROM engine_outbox WHERE kind='start' AND session_id=$session AND state='pending'",
            r => r.Text(0), default, new SqlArg("$session", sessionId));
        foreach (var id in starts)
        {
            var start = f.ClaimEffect(id);
            Assert.True(f.Complete(start, await f.Effects.ExecuteAsync(start, default)).Applied);
        }
        var message = f.Queue(refs: ["refB", "refA"], sessionId: sessionId); var first = f.Claim(message);
        var executing = f.Effects.ExecuteAsync(first, default); var agent = await f.Launcher.NextAsync();
        try
        {
            var initialize = await agent.ReadAsync("initialize"); var done = f.Hosts.For(sessionId).CurrentTurn.Done;
            await agent.ReplyErrorAsync(initialize, -32000, "Sign in");
            var refusal = await executing.WaitAsync(TimeSpan.FromSeconds(8)); await done.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(f.Complete(first, refusal).Applied);
        }
        finally { agent.Crash(); }
        SignIn(f);
        using var snapshot = f.Db.OpenSnapshot(default);
        var user = f.Rows.UserDeliveryOfClient(snapshot, sessionId, "C")!;
        return (user, f.Claim(message));
    }

    internal static void SignIn(AdapterDeliveryFixture f)
    {
        var need = f.Db.QueryFirst("SELECT id FROM engine_needs_me WHERE state='open'", r => r.Text(0), default)!;
        Assert.True(f.Apply(new StartSignIn("claude", "", null, need), "operation", 0, new(ActorKind.Window, "owner")).Applied);
        _ = f.ClaimEffect("begin_sign_in/operation");
        var binding = new SignInAttemptBinding("operation", "claude/", "begin_sign_in/operation", 1);
        Assert.True(f.Apply(new SignInProgress("finishing", null, binding), "operation", 1, new(ActorKind.System, "sign_in.effects")).Applied);
        Assert.True(f.Apply(new SignInProgress("signed_in", null, binding), "operation", 2, new(ActorKind.System, "sign_in.effects")).Applied);
    }
}
