using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Recording;
using Assembly.Engine.Sessions;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth.Identity;

// Existing command JSON supplies the reviewed optional fields; no stub/new API is used for this baseline.
public sealed class AcceptedIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepted_identity_only_change_is_not_lost_when_capability_already_matches(bool steer)
    {
        using var f = Fixture(); f.Db.Writer.Write("test.steer-before-open", s => s.Execute("UPDATE engine_session SET can_steer=$steer WHERE id='s'", new SqlArg("$steer", steer ? 1 : 0)));
        var before = Stable(f); var result = f.Apply(Fact(steer, "actual-open", Observed()), "s", actor: Adapter);
        Assert.True(result.Applied, result.Error); Assert.Equal("actual-open", f.Text("SELECT provider_session_id FROM engine_session WHERE id='s'")); Assert.Equal(1, result.Version);
        Assert.Equal(before, Stable(f)); Assert.Single(f.Events()); Assert.Equal("session.changed", f.Events()[0].Kind); Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void New_open_replaces_accepted_identity_and_unchanged_pair_emits_no_second_event()
    {
        using var f = Fixture(); Assert.True(f.Apply(Fact(true, "first", Observed()), "s", actor: Adapter).Applied);
        Assert.Equal("first", f.Text("SELECT provider_session_id FROM engine_session WHERE id='s'"));
        var duplicate = f.Apply(Fact(true, "first", Observed()), "s", 1, actor: Adapter); Assert.True(duplicate.Applied); Assert.Equal(1, duplicate.Version); Assert.Equal(1, f.Count("engine_event"));
        Assert.True(f.Apply(Fact(true, "second", Observed()), "s", 1, actor: Adapter).Applied); Assert.Equal("second", f.Text("SELECT provider_session_id FROM engine_session WHERE id='s'")); Assert.Equal(2, f.Count("engine_event"));
    }

    [Fact]
    public void Identity_fact_changes_no_workflow_state_or_message_reservation()
    {
        using var f = Fixture(); f.SeedRun(state: "running"); f.SeedStep(state: "waiting", sessionId: "s", attempt: 2);
        f.Db.Writer.Write("test.forbid-identity-workflow-side-effect", s => s.Execute("CREATE TRIGGER reject_identity_run_update BEFORE UPDATE ON engine_run BEGIN SELECT RAISE(ABORT,'identity touched workflow'); END"));
        var before = Stable(f); Assert.True(f.Apply(Fact(true, "accepted", new("s", "r", "step", 2, 0)), "s", actor: Adapter).Applied);
        Assert.Equal(before, Stable(f)); Assert.Equal("running", f.State("engine_run", "r")); Assert.Equal("waiting", f.State("engine_step", "step")); Assert.Single(f.Events()); Assert.Equal("session.changed", f.Events()[0].Kind);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("window")]
    [InlineData("holder")]
    [InlineData("reservation")]
    [InlineData("epoch")]
    [InlineData("machine")]
    [InlineData("ended")]
    [InlineData("handoff")]
    [InlineData("observation-session")]
    [InlineData("observation-run")]
    [InlineData("observation-step")]
    [InlineData("observation-attempt")]
    public void Wrong_actor_owner_or_full_nullable_tuple_refuses_without_identity_or_event(string change)
    {
        using var f = Fixture(); var observation = Observed(); var context = Context(f);
        switch (change)
        {
            case "actor": context = context with { Actor = new(ActorKind.System, "other") }; break;
            case "window": context = context with { Actor = new(ActorKind.Window, "owner") }; break;
            case "epoch": observation = observation with { Epoch = 1 }; break;
            case "observation-session": observation = observation with { SessionId = "other" }; break;
            case "observation-run": observation = observation with { RunId = "wrong" }; break;
            case "observation-step": observation = observation with { StepId = "wrong" }; break;
            case "observation-attempt": observation = observation with { Attempt = 1 }; break;
            default:
                f.Db.Writer.Write("test.identity-owner-change", s => s.Execute(change switch
                {
                    "holder" => "UPDATE engine_session SET holder='remote' WHERE id='s'",
                    "reservation" => "UPDATE engine_session SET reserved_epoch=1 WHERE id='s'",
                    "machine" => "UPDATE engine_session SET machine='other' WHERE id='s'",
                    "ended" => "UPDATE engine_session SET state='ended' WHERE id='s'",
                    _ => "UPDATE engine_session SET state='handing_off' WHERE id='s'"
                })); break;
        }
        Assert.False(f.Apply(context, Fact(true, "wrong", observation)).Applied); Assert.Null(f.Text("SELECT provider_session_id FROM engine_session WHERE id='s'")); Assert.Equal(0, f.Count("engine_event")); Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Blank_accepted_identity_is_not_a_capability_only_fact(string id)
    { using var f = Fixture(); Assert.False(f.Apply(Fact(true, id, Observed()), "s", actor: Adapter).Applied); Assert.Equal(0, f.Count("engine_event")); }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mixed_optional_arguments_refuse(bool missingIdentity)
    { using var f = Fixture(); Assert.False(f.Apply(Fact(true, missingIdentity ? null : "actual", missingIdentity ? Observed() : null), "s", actor: Adapter).Applied); Assert.Equal(0, f.Count("engine_event")); }

    [Fact]
    public void Current_step_capture_is_exact_and_nulls_cannot_stand_for_a_scoped_step()
    {
        using var f = Fixture(); f.SeedRun(); f.SeedStep("a", "r", "waiting", "s", 2); f.SeedStep("b", "r", "running", "s", 3);
        Assert.False(f.Apply(Fact(true, "wrong", new("s", "r", "b", 3, 0)), "s", actor: Adapter).Applied);
        Assert.False(f.Apply(Fact(true, "missing-step", new("s", "r", null, null, 0)), "s", actor: Adapter).Applied);
        Assert.True(f.Apply(Fact(true, "actual", new("s", "r", "a", 2, 0)), "s", actor: Adapter).Applied); Assert.Equal("actual", f.Text("SELECT provider_session_id FROM engine_session WHERE id='s'"));
    }

    [Fact]
    public void Saved_context_replay_precedes_changed_owner_and_changed_identity_conflicts()
    {
        using var f = Fixture(); var context = Context(f); var command = Fact(true, "first", Observed()); var first = f.Apply(context, command); Assert.True(first.Applied);
        Assert.Equal("first", f.Text("SELECT provider_session_id FROM engine_session WHERE id='s'"));
        f.Db.Writer.Write("test.identity-after-commit-owner", s => s.Execute("UPDATE engine_session SET holder='remote',epoch=epoch+1 WHERE id='s'"));
        Assert.Equal(first, f.Apply(context, command)); Assert.Equal("command_id_conflict", f.Apply(context, Fact(true, "changed", Observed())).Error); Assert.Equal(1, f.Count("engine_event"));
    }

    [Fact]
    public void Identity_event_failure_rolls_back_identity_capability_version_and_receipt_before_exact_retry()
    {
        using var f = Fixture(); var context = Context(f); var command = Fact(true, "actual", Observed());
        f.Db.Writer.Write("test.identity-write-fault", s => s.Execute("CREATE TRIGGER fail_identity_event BEFORE INSERT ON engine_event BEGIN SELECT RAISE(ABORT,'identity event fault'); END"));
        Assert.Throws<SqliteException>(() => f.Apply(context, command)); Assert.Null(f.Text("SELECT provider_session_id FROM engine_session WHERE id='s'")); Assert.Equal(0, f.Number("SELECT version FROM engine_session WHERE id='s'")); Assert.Equal(0, f.Count("engine_command_receipt"));
        f.Db.Writer.Write("test.identity-write-unfault", s => s.Execute("DROP TRIGGER fail_identity_event")); Assert.True(f.Apply(context, command).Applied); Assert.Equal("actual", f.Text("SELECT provider_session_id FROM engine_session WHERE id='s'")); Assert.Equal(1, f.Count("engine_command_receipt"));
    }

    [Fact]
    public void Old_bool_only_canonical_and_broad_system_actor_behavior_remain_exact()
    {
        Assert.Equal("{\"kind\":\"report_capabilities\",\"canSteer\":true}", JsonSerializer.Serialize<EngineCommand>(new ReportCapabilities(true), EngineJson.Default.EngineCommand));
        using var f = Fixture(); Assert.True(f.Apply(new ReportCapabilities(true), "s", actor: new(ActorKind.System, "initialize")).Applied); Assert.Equal(1, f.Number("SELECT can_steer FROM engine_session WHERE id='s'")); Assert.Null(f.Text("SELECT provider_session_id FROM engine_session WHERE id='s'"));
    }

    private static readonly EngineActor Adapter = new(ActorKind.System, "adapter");
    private static Attribution Observed() => new("s", null, null, null, 0);
    private static EngineFixture Fixture() { var f = new EngineFixture(); f.SeedSession(); return f; }
    private static CommandContext Context(EngineFixture f) => new(Guid.NewGuid(), f.Machine, Adapter, "s", 0, 0, f.Time.GetUtcNow().ToUnixTimeMilliseconds());
    private static ReportCapabilities Fact(bool steer, string? identity, Attribution? observation)
    {
        var node = JsonSerializer.SerializeToNode(new ReportCapabilities(steer), EngineJson.Default.ReportCapabilities)!;
        if (identity is not null) node["providerSessionId"] = identity;
        if (observation is not null) node["observation"] = JsonSerializer.SerializeToNode(observation, EngineJson.Default.Attribution);
        return JsonSerializer.Deserialize(node.ToJsonString(), EngineJson.Default.ReportCapabilities)!;
    }
    private static string Stable(EngineFixture f) => f.Text("SELECT json_array(state,activity_state,cancel_requested,reserved_epoch,run_id,(SELECT COUNT(*) FROM engine_message),(SELECT COUNT(*) FROM engine_outbox),(SELECT COUNT(*) FROM engine_needs_me),(SELECT COUNT(*) FROM engine_provider)) FROM engine_session WHERE id='s'")!;
}
