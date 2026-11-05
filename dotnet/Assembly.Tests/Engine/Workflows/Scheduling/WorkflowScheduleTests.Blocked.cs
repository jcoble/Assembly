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
    [InlineData("DELETE FROM engine_provider", "codex hasn't been checked on this machine.")]
    public void A_step_its_provider_or_model_cant_run_fails_with_the_reason_and_needs_the_owner(string change, string reason)
    {
        using var f = new Fixture();
        Providers(f, change);
        Assert.True(Block(f));
        Assert.Equal(("failed", reason, true), Blocked(f));
        Assert.Equal(0, f.Core.Count("engine_step_attempt"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_event WHERE kind='step.changed' AND entity_id=$id AND entity_version=(SELECT version FROM engine_step WHERE id=$id)", new SqlArg("$id", f.Step)));
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
    public void Retry_tries_once_more_and_fails_loud_again_or_starts_once_it_can()
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
        Retry();
        Assert.Equal(("pending", null, false), Blocked(f));
        Assert.True(Block(f));
        Assert.Equal("failed", Blocked(f).State);

        Retry();
        Providers(f, "UPDATE engine_provider SET auth_state='signed_in'");
        Assert.False(Block(f));
        Assert.True(f.Apply(f.Prepare()).Result.Applied);
        Assert.Equal(1, f.Core.Count("engine_step_attempt"));
    }
}
