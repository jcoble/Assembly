using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Reading;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Tests.Engine.Workflows.Scheduling;

/// <summary>A step that can't start because of its provider or model fails loud, names which, and doesn't try again on its own.</summary>
public sealed partial class WorkflowScheduleTests
{
    private static bool Block(Fixture f) =>
        f.Core.Db.Writer.Write("test.block", s => f.Commands.BlockUnavailableStep(s, f.Step, 2000));

    private static (string State, string? Explanation, bool NeedsOwner) Blocked(Fixture f)
    {
        var row = new EngineReader(f.Core.Db, f.Core.Machine).Entity("step", f.Step, default)!.Value;
        return (row.GetProperty("state").GetString()!, row.GetProperty("explanation").GetString(), row.GetProperty("needsOwner").GetBoolean());
    }

    private static void Providers(Fixture f, string sql) => f.Core.Db.Writer.Write("test.provider", s => s.Execute(sql));

    [Theory]
    [InlineData("UPDATE engine_provider SET facts_json='{\"models\":[{\"id\":\"other\",\"name\":\"Other\"}]}'", "Model selected-model isn't available for codex on this machine.")]
    [InlineData("UPDATE engine_provider SET auth_state='needs_sign_in'", "Sign in to codex to run this step.")]
    [InlineData("UPDATE engine_provider SET installation='missing'", "codex isn't installed on this machine.")]
    [InlineData("UPDATE engine_provider SET auth_state='could_not_check'", "Couldn't check whether codex is signed in on this machine.")]
    [InlineData("UPDATE engine_provider SET installation='unknown',auth_state='could_not_check',facts_json=NULL,checked_ms=1500", "Couldn't tell whether codex is installed on this machine.")]
    public void A_step_its_provider_or_model_cant_run_fails_with_the_reason_and_needs_the_owner(string change, string reason)
    {
        using var f = new Fixture();
        Providers(f, change);
        Assert.True(Block(f));
        Assert.Equal(("failed", reason, true), Blocked(f));
        Assert.Equal(0, f.Core.Count("engine_step_attempt"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_event WHERE kind='step.changed' AND entity_id=$id AND entity_version=(SELECT version FROM engine_step WHERE id=$id)", new SqlArg("$id", f.Step)));
    }

    [Theory]
    [InlineData("UPDATE engine_provider SET auth_state='checking'")]
    [InlineData("UPDATE engine_provider SET installation='unknown',auth_state='checking',facts_json=NULL")]
    public void A_step_waits_while_its_provider_is_being_checked_and_starts_once_the_check_says_it_can(string checking)
    {
        using var f = new Fixture();
        var facts = f.Core.Text("SELECT facts_json FROM engine_provider");
        Providers(f, checking);
        Assert.False(Block(f));
        Assert.Equal(("pending", null, false), Blocked(f));
        f.Core.Db.Writer.Write("test.provider", s => s.Execute(
            "UPDATE engine_provider SET installation='installed',auth_state='signed_in',facts_json=$facts", new SqlArg("$facts", facts)));
        Assert.False(Block(f));
        Assert.True(f.Apply(f.Prepare()).Result.Applied);
    }

    private const string CheckedFine = "UPDATE engine_provider SET installation='installed',auth_state='signed_in',checked_ms=2500,facts_json='{\"models\":[{\"id\":\"selected-model\",\"name\":\"Selected\"}]}'";

    private static long ProbesAsked(Fixture f) => f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='probe_provider'");

    [Theory]
    [InlineData("DELETE FROM engine_provider")]
    [InlineData("UPDATE engine_provider SET installation='unknown',auth_state='unverified',facts_json=NULL,checked_ms=NULL")]
    public void A_step_on_a_provider_nobody_checked_has_the_engine_check_it_and_goes_on(string unchecked_)
    {
        using var f = new Fixture();
        Providers(f, unchecked_);
        Assert.False(Block(f));
        Assert.Equal(("pending", null, false), Blocked(f));
        Assert.Equal("checking", f.Core.Text("SELECT auth_state FROM engine_provider"));
        Assert.Equal(1, ProbesAsked(f));
        // Asking again while it runs starts nothing more.
        Assert.False(Block(f));
        Assert.Equal(1, ProbesAsked(f));
        Providers(f, CheckedFine);
        Assert.False(Block(f));
        Assert.True(f.Apply(f.Prepare()).Result.Applied);
    }

    [Theory]
    [InlineData("installation='not_installed',auth_state='could_not_check'", "codex isn't installed on this machine.")]
    [InlineData("installation='installed',auth_state='needs_sign_in',facts_json='{\"models\":[]}'", "Sign in to codex to run this step.")]
    [InlineData("installation='installed',auth_state='signed_in',facts_json='{\"models\":[]}'", "Model selected-model isn't available for codex on this machine.")]
    public void A_check_the_engine_ran_for_a_step_fails_it_loud_with_what_it_found_and_only_once(string found, string reason)
    {
        using var f = new Fixture();
        Providers(f, "DELETE FROM engine_provider");
        Assert.False(Block(f));
        Providers(f, $"UPDATE engine_provider SET {found},checked_ms=2500");
        Assert.True(Block(f));
        Assert.Equal(("failed", reason, true), Blocked(f));
        Assert.Equal(1, ProbesAsked(f));
    }

    [Fact]
    public void A_step_on_a_provider_this_machine_has_no_setup_for_fails_without_a_check()
    {
        using var f = new Fixture(unconfigured: true);
        Assert.True(Block(f));
        Assert.Equal(("failed", "codex isn't set up on this machine.", true), Blocked(f));
        Assert.Equal(0, ProbesAsked(f));
    }

    [Fact]
    public void A_step_that_can_start_is_left_alone()
    {
        using var f = new Fixture();
        Assert.False(Block(f));
        Assert.Equal("pending", Blocked(f).State);
        Assert.NotNull(f.Prepare());
    }

    [Fact]
    public void Retry_checks_the_provider_again_then_fails_loud_again_or_starts_once_it_can()
    {
        using var f = new Fixture();
        Providers(f, "UPDATE engine_provider SET auth_state='needs_sign_in'");
        Assert.True(Block(f));
        void Retry()
        {
            var context = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.Window, "owner"), f.Run,
                f.Core.Number("SELECT version FROM engine_run"), 0, 3000);
            var result = f.Core.Apply(context, new RetryWorkflowStep(f.Step));
            Assert.True(result.Applied, result.Error);
        }
        // What stopped it may have changed, and nothing else would look: the person pressed Retry because they fixed it.
        Retry();
        Assert.Equal(("pending", null, false), Blocked(f));
        Assert.False(Block(f));
        Assert.Equal("checking", f.Core.Text("SELECT auth_state FROM engine_provider"));
        Assert.Equal(1, ProbesAsked(f));
        Providers(f, "UPDATE engine_provider SET auth_state='needs_sign_in',checked_ms=3500");
        Assert.True(Block(f));
        Assert.Equal("failed", Blocked(f).State);

        Retry();
        Assert.False(Block(f));
        Assert.Equal(2, ProbesAsked(f));
        Providers(f, CheckedFine);
        Assert.False(Block(f));
        Assert.True(f.Apply(f.Prepare()).Result.Applied);
        Assert.Equal(1, f.Core.Count("engine_step_attempt"));
    }
}
