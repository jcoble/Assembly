using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Runs;
using Assembly.Engine.Reading;
using Assembly.Engine.Workflows.Runs;
using Assembly.Infrastructure.Phone;
using static Assembly.Tests.Engine.Workflows.Execution.CommandStepTests;

namespace Assembly.Tests.Engine.Workflows.Execution;

/// <summary>What the engine says needs the owner: every client counts from these fields and works nothing out itself.</summary>
public sealed class OwnerAttentionTests
{
    private static JsonElement Row(Fixture f, string kind, string id) =>
        new EngineReader(f.Core.Db, f.Core.Machine).Entity(kind, id, default)!.Value;

    private static IReadOnlyList<string> Inbox(Fixture f)
    {
        using var scope = f.Core.Db.OpenSnapshot(default);
        return PhoneNeedsRoutes.Read(scope, f.Core.Machine).Select(item => item.Id).ToArray();
    }

    internal static async Task<Fixture> FailedWith(int attempts)
    {
        var f = new Fixture([], "allow", flow: $"name: Flow\npolicy: {{default: allow}}\nsteps:\n  checks:\n    max_attempts: {attempts}\n    run: [\"exit 3\"]\n");
        Assert.True(f.Start(f.Prepare()).Applied);
        var ran = f.Claim();
        Assert.True(f.Complete(ran, await f.Host().ExecuteAsync(ran, default)).Applied);
        Assert.Equal("failed", f.Core.Text("SELECT state FROM engine_step"));
        return f;
    }

    [Fact]
    public async Task A_command_approval_and_a_gate_are_for_the_owner_and_their_step_isnt_counted_twice()
    {
        using var f = ApprovalBesideCommand();
        var (command, gate, _, _) = await BothAsking(f, settled: true);
        Assert.True(Row(f, "needs_me", command).GetProperty("forOwner").GetBoolean());
        Assert.True(Row(f, "needs_me", gate).GetProperty("forOwner").GetBoolean());
        Assert.False(Row(f, "step", f.Step).GetProperty("needsOwner").GetBoolean());
    }

    [Fact]
    public async Task A_question_a_session_answers_is_not_for_the_owner()
    {
        using var f = ApprovalBesideCommand();
        var (command, _, _, _) = await BothAsking(f, settled: true);
        f.Core.SeedSession("asker");
        f.Core.Db.Writer.Write("test.asker", s => s.Execute(
            "UPDATE engine_needs_me SET session_id='asker',source_kind='provider',addressee_session_id='owner' WHERE id=$id", new SqlArg("$id", command)));
        Assert.False(Row(f, "needs_me", command).GetProperty("forOwner").GetBoolean());
        Assert.DoesNotContain(command, Inbox(f));
    }

    [Fact]
    public async Task A_failed_step_with_attempts_left_needs_the_owner_until_it_is_retried()
    {
        using var f = await FailedWith(attempts: 2);
        Assert.True(Row(f, "step", f.Step).GetProperty("needsOwner").GetBoolean());
        Assert.Equal("owner", Row(f, "step", f.Step).GetProperty("ownerSessionId").GetString());
        var retry = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.Window, "owner"), f.Run,
            f.Core.Number("SELECT version FROM engine_run"), 0, 2000);
        Assert.True(f.Core.Apply(retry, new RetryWorkflowStep(f.Step)).Applied);
        Assert.False(Row(f, "step", f.Step).GetProperty("needsOwner").GetBoolean());
    }

    [Fact]
    public async Task Ending_the_run_sends_its_stopped_step_again_so_nobody_keeps_counting_it()
    {
        using var f = await FailedWith(attempts: 2);
        var before = f.Core.Number("SELECT version FROM engine_step WHERE id=$id", new SqlArg("$id", f.Step));
        var cancel = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.Window, "owner"), f.Run,
            f.Core.Number("SELECT version FROM engine_run"), 0, 2000);
        Assert.True(f.Core.Apply(cancel, new CancelRun()).Applied);
        Assert.False(Row(f, "step", f.Step).GetProperty("needsOwner").GetBoolean());
        Assert.True(f.Core.Number("SELECT version FROM engine_step WHERE id=$id", new SqlArg("$id", f.Step)) > before);
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_event WHERE kind='step.changed' AND entity_id=$id AND entity_version=(SELECT version FROM engine_step WHERE id=$id)", new SqlArg("$id", f.Step)));
    }

    [Fact]
    public async Task A_run_that_fails_closes_the_questions_nobody_can_answer_any_more()
    {
        using var f = new Fixture([], "allow", first: "asks", flow: "name: Flow\npolicy:\n  default: allow\n  commands:\n    ask: ['printf*']\nsteps:\n  asks:\n    run: [\"printf asked\"]\n  fails:\n    run: [\"exit 3\"]\n");
        var asked = f.StartStep("asks");
        Assert.True(f.Complete(asked, await f.Host().ExecuteAsync(asked, default)).Applied);
        var need = f.Core.Text("SELECT id FROM engine_needs_me WHERE state='open'")!;
        Assert.Contains(need, Inbox(f));
        var failing = f.StartStep("fails");
        Assert.True(f.Complete(failing, await f.Host().ExecuteAsync(failing, default)).Applied);
        Assert.Equal("failed", f.Core.Text("SELECT state FROM engine_run"));
        Assert.Equal("cancelled", Row(f, "needs_me", need).GetProperty("state").GetString());
        Assert.DoesNotContain(need, Inbox(f));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_event WHERE kind='needs_me.cancelled' AND entity_id=$id", new SqlArg("$id", need)));
    }

    [Fact]
    public async Task A_failed_step_with_no_attempts_left_ends_the_run_and_asks_for_nothing()
    {
        using var f = await FailedWith(attempts: 1);
        Assert.False(Row(f, "step", f.Step).GetProperty("needsOwner").GetBoolean());
    }
}
