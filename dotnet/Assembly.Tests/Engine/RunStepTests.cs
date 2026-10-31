using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Runs;
using Assembly.Application.Features.Engine.Sessions;

namespace Assembly.Tests.Engine;

public sealed class RunStepTests
{
    private static readonly EngineActor System = new(ActorKind.System, "workflow");
    private static StepDefinition Def(string id, bool required = true, int max = 1, bool skip = false,
        params Dependency[] dependencies) => new(id, id, required, max, skip, dependencies);
    private static void Create(EngineFixture f, params StepDefinition[] steps)
    {
        f.SeedSession("owner");
        Assert.True(f.Apply(new StartRun("owner", "https://example.com/task", "/repo", "branch", 100, steps), "r").Applied);
        f.SeedSession("worker", parentId: "owner", runId: "r");
    }
    private static CommandResult Start(EngineFixture f, string id = "s") =>
        f.Apply(new StartStep("worker"), id, Version(f, id));
    private static long Version(EngineFixture f, string id) => f.Number("SELECT version FROM engine_step WHERE id=$id", new SqlArg("$id", id));
    private static CommandResult Set(EngineFixture f, string state, string id = "s", int attempt = 1,
        string[]? proof = null, bool rule = false, EngineActor? actor = null) =>
        f.Apply(new SetStepState(state, attempt, proof ?? ["https://example.com/proof"], "reason", rule),
            id, Version(f, id), actor: actor ?? System);

    [Fact]
    public void Dependencies_attempts_and_receipts_govern_progress()
    {
        using var f = new EngineFixture(); Create(f, Def("dep"), Def("s", max: 2, dependencies: [new("dep", false)]));
        Assert.Equal("dependency_not_ready", Start(f).Error);
        Assert.True(Start(f, "dep").Applied); Assert.True(Set(f, "succeeded", "dep").Applied);
        var id = Guid.NewGuid(); var version = Version(f, "s");
        var first = f.Apply(new StartStep("worker"), "s", version, commandId: id);
        Assert.Equal(first, f.Apply(new StartStep("worker"), "s", version, commandId: id));
        Assert.True(Set(f, "failed").Applied); Assert.True(Start(f).Applied);
        Assert.Equal(2, f.Number("SELECT attempt FROM engine_step WHERE id='s'"));
        Assert.Equal(2, f.Number("SELECT COUNT(*) FROM engine_step_attempt WHERE step_id='s'"));
        Assert.Equal("proof_required", Set(f, "succeeded", attempt: 2, proof: []).Error);
        Assert.True(Set(f, "succeeded", attempt: 2).Applied);
    }

    [Fact]
    public void Only_required_steps_choose_run_outcome()
    {
        using var f = new EngineFixture(); Create(f, Def("optional", false), Def("s"), Def("skip", skip: true));
        Assert.True(Start(f, "optional").Applied); Assert.True(Set(f, "failed", "optional").Applied);
        Assert.Equal("running", f.State("engine_run", "r"));
        Assert.True(Set(f, "skipped", "skip", attempt: 0, rule: true).Applied);
        Assert.True(Start(f).Applied); Assert.True(Set(f, "succeeded").Applied);
        Assert.Equal("succeeded", f.State("engine_run", "r"));
        using var exhausted = new EngineFixture(); Create(exhausted, Def("s"));
        Assert.True(Start(exhausted).Applied); Assert.True(Set(exhausted, "failed").Applied);
        Assert.Equal("failed", exhausted.State("engine_run", "r"));
    }

    [Fact]
    public void Cancellation_waits_and_late_facts_do_not_reopen()
    {
        using var f = new EngineFixture(); Create(f, Def("s"), Def("pending")); Assert.True(Start(f).Applied);
        var oldVersion = Version(f, "s");
        Assert.True(f.Apply(new CancelRun(), "r", version: 2).Applied);
        Assert.Equal("cancelling", f.State("engine_run", "r"));
        Assert.Equal("run_cancelling", Start(f, "pending").Error);
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "worker", version: 1, actor: System).Applied);
        Assert.Equal("cancelling", f.State("engine_run", "r"));
        f.Db.Writer.Write("drain", s =>
        {
            s.Execute("UPDATE engine_outbox SET state='done'");
            var c = new CommandContext(Guid.NewGuid(), f.Machine, System, "r", 3, 0, 1000);
            var events = RunTransitions.Recompute(s, c, "r");
            return Assembly.Application.Features.Engine.Persistence.EngineJournal.Commit(s, c, 4, 0, events, []);
        });
        Assert.Equal("cancelled", f.State("engine_run", "r"));
        var late = f.Apply(new SetStepState("succeeded", 1, ["https://example.com/proof"], null, false), "s", oldVersion, actor: System);
        Assert.True(late.Late); Assert.Equal("cancelled", f.State("engine_step", "s"));
        Assert.Equal(1, f.Number("SELECT late FROM engine_step_fact"));
        Assert.Equal("step.late", f.Events().Last().Kind);
    }
    [Fact]
    public void Cancel_leaves_failed_step_and_attempt_as_failed()
    {
        using var f = new EngineFixture(); Create(f, Def("failed", false), Def("s"));
        f.SeedSession("worker2", parentId: "owner", runId: "r");
        Assert.True(Start(f, "failed").Applied);
        Assert.True(Set(f, "failed", "failed").Applied);
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "worker", actor: System).Applied);
        Assert.True(f.Apply(new StartStep("worker2"), "s", 1).Applied);
        var failedVersion = Version(f, "failed");

        Assert.True(f.Apply(new CancelRun(), "r", 2).Applied);
        Assert.Equal("failed", f.State("engine_step", "failed"));
        Assert.Equal(failedVersion, Version(f, "failed"));
        Assert.Equal("failed", f.Text("SELECT state FROM engine_step_attempt WHERE step_id='failed'"));
        f.Db.Writer.Write("drain", s => s.Execute("UPDATE engine_outbox SET state='done'"));
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "worker2", 1, actor: System).Applied);

        Assert.Equal("cancelled", f.State("engine_run", "r"));
        Assert.Equal("failed", f.State("engine_step", "failed"));
        Assert.Equal(failedVersion, Version(f, "failed"));
        Assert.Equal("failed", f.Text("SELECT state FROM engine_step_attempt WHERE step_id='failed'"));
        Assert.Equal("cancelled", f.State("engine_step", "s"));
        Assert.Equal("cancelled", f.Text("SELECT state FROM engine_step_attempt WHERE step_id='s'"));
    }

    private static CommandResult Expire(EngineFixture f, EngineActor? actor = null, long? version = null) =>
        f.Db.Writer.Write("expire", s => RunTransitions.ExpireLostOwner(s,
            new(Guid.NewGuid(), f.Machine, actor ?? System, "r", version ?? f.Number("SELECT version FROM engine_run"), 0,
                f.Time.GetUtcNow().ToUnixTimeMilliseconds()), "r"));

    [Fact]
    public void Owner_deadline_is_persisted_and_rechecked_after_resume()
    {
        using var f = new EngineFixture(); Create(f, Def("s")); Assert.True(Start(f).Applied);
        Assert.True(f.Apply(new SetSessionState("lost", null, null), "owner", actor: System).Applied);
        Assert.Equal(1000, f.Number("SELECT owner_lost_ms FROM engine_run"));
        Assert.Equal(3, f.Number("SELECT version FROM engine_run"));
        f.Time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.Equal("owner_not_expired", Expire(f).Error);
        Assert.Equal("running", f.State("engine_run", "r"));
        f.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("forbidden", Expire(f, new(ActorKind.Window, "window")).Error);
        Assert.Equal("version_conflict", Expire(f, version: 99).Error);
        Assert.True(Expire(f).Applied); Assert.Equal("failed", f.State("engine_run", "r"));
        using var resumed = new EngineFixture(); Create(resumed, Def("s")); Assert.True(Start(resumed).Applied);
        Assert.True(resumed.Apply(new SetSessionState("lost", null, null), "owner", actor: System).Applied);
        resumed.Time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.True(resumed.Apply(new SetSessionState("idle", null, null), "owner", 1, actor: System).Applied);
        resumed.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(resumed.Text("SELECT owner_lost_ms FROM engine_run"));
        Assert.Equal("owner_not_expired", Expire(resumed).Error);
        Assert.Equal("running", resumed.State("engine_run", "r"));
    }

    public static IEnumerable<object[]> Edges()
    {
        string[] states = ["pending", "running", "waiting", "failed", "succeeded", "skipped", "cancelled"];
        foreach (var from in states)
            foreach (var to in states)
                yield return [from, to, (from, to) is ("pending", "skipped") or
                    ("running", "waiting" or "failed" or "succeeded" or "cancelled") or
                    ("waiting", "running" or "failed" or "succeeded" or "cancelled")];
    }

    [Theory]
    [MemberData(nameof(Edges))]
    public void Step_transition_table_and_refusals_preserve_state(string from, string to, bool allowed)
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedRun(ownerSessionId: "owner", state: "running");
        f.SeedSession("worker", parentId: "owner", runId: "r");
        f.SeedStep("s", state: from, sessionId: "worker", attempt: from == "pending" ? 0 : 1, maxAttempts: 2, ruleSkippable: true);
        var result = Set(f, to, attempt: from == "pending" ? 0 : 1, rule: true,
            actor: to == "skipped" ? System : new(ActorKind.Session, "worker"));
        if (to == "skipped" && from is "succeeded" or "skipped" or "cancelled")
        {
            Assert.True(result.Late); Assert.Equal(from, f.State("engine_step", "s"));
            Assert.Single(f.Events()); Assert.Equal("step.late", f.Events()[0].Kind);
            return;
        }
        Assert.Equal(allowed, result.Applied);
        Assert.Equal(allowed ? to : from, f.State("engine_step", "s"));
        if (!allowed) { Assert.Empty(f.Events()); Assert.Equal(0, Version(f, "s")); Assert.Equal(0, f.Count("engine_outbox")); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cycle")]
    [InlineData("max")]
    [InlineData("duplicate")]
    [InlineData("duplicate_dependency")]
    public void Invalid_definitions_do_not_create_rows(string kind)
    {
        using var f = new EngineFixture(); f.SeedSession("owner");
        StepDefinition[] steps = kind switch
        {
            "missing" => [Def("s", dependencies: [new("absent", false)])],
            "cycle" => [Def("s", dependencies: [new("b", false)]), Def("b", dependencies: [new("s", true)])],
            "max" => [Def("s", max: 0)],
            "duplicate" => [Def("s"), Def("s")],
            _ => [Def("s", dependencies: [new("b", true), new("b", false)]), Def("b")]
        };
        Assert.Equal("bad_args", f.Apply(new StartRun("owner", "https://example.com", null, null, 100, steps), "r").Error);
        Assert.Equal(0, f.Count("engine_run")); Assert.Equal(0, f.Count("engine_step"));
        Assert.Equal(0, f.Count("engine_step_dependency")); Assert.Empty(f.Events());
    }

    [Fact]
    public void Optional_dependencies_and_retry_exhaustion()
    {
        using var f = new EngineFixture(); Create(f, Def("dep", false), Def("s", max: 2, dependencies: [new("dep", true)]));
        Assert.True(Start(f).Applied); Assert.True(Set(f, "failed").Applied);
        Assert.Equal("running", f.State("engine_run", "r"));
        Assert.True(Start(f).Applied); Assert.True(Set(f, "failed", attempt: 2).Applied);
        var count = f.Count("engine_event");
        Assert.Equal("invalid_transition", Start(f).Error);
        Assert.Equal(count, f.Count("engine_event")); Assert.Equal(2, f.Count("engine_step_attempt"));
    }

    [Fact]
    public void Wrong_worker_run_attempt_epoch_and_untrusted_skip_are_refused()
    {
        using var f = new EngineFixture(); Create(f, Def("s", skip: true));
        f.SeedSession("other"); f.SeedSession("wrong-run", parentId: "owner");
        Assert.Equal("forbidden", f.Apply(new StartStep("other"), "s", 1).Error);
        Assert.Equal("forbidden", f.Apply(new StartStep("wrong-run"), "s", 1).Error);
        Assert.Equal("forbidden", f.Apply(new StartStep("worker"), "s", 1, actor: new(ActorKind.Session, "other")).Error);
        Assert.Equal("forbidden", Set(f, "skipped", attempt: 0, rule: true, actor: new(ActorKind.Session, "worker")).Error);
        Assert.True(Start(f).Applied);
        var count = f.Count("engine_event");
        Assert.Equal("forbidden", Set(f, "succeeded", actor: new(ActorKind.Session, "other")).Error);
        Assert.Equal("wrong_attempt", Set(f, "succeeded", attempt: 2).Error);
        Assert.Equal("proof_required", Set(f, "succeeded", proof: ["http://example.com", "/relative"]).Error);
        Assert.Equal("reason_required", f.Apply(new SetStepState("failed", 1, [], null, false), "s", Version(f, "s"), actor: System).Error);
        Assert.Equal("stale_epoch", f.Apply(new SetStepState("succeeded", 1, ["https://example.com"], null, false), "s", Version(f, "s"), epoch: 2, actor: System).Error);
        Assert.Equal(count, f.Count("engine_event")); Assert.Equal("running", f.State("engine_step", "s"));
        Assert.True(Set(f, "succeeded", actor: new(ActorKind.Session, "worker")).Applied);
        Assert.Equal("wrong_attempt", Set(f, "failed", attempt: 2).Error);
        Assert.Equal("forbidden", Set(f, "succeeded", actor: new(ActorKind.Session, "other")).Error);
    }

    [Fact]
    public void Json_cannot_grant_workflow_privilege_and_runs_replay_after_reopen()
    {
        using var f = new EngineFixture(); f.SeedSession("owner");
        var command = new StartRun("owner", "https://example.com", null, null, 100, [Def("s", skip: true)]);
        var id = Guid.NewGuid(); var result = f.Apply(command, "r", commandId: id);
        f.Reopen(); Assert.Equal(result, f.Apply(command, "r", commandId: id));
        var json = """{"kind":"set_step_state","state":"skipped","attempt":0,"proofUrls":[],"reason":"rule","byWorkflowRule":true}""";
        var fact = global::System.Text.Json.JsonSerializer.Deserialize(json, Assembly.Application.Features.Engine.EngineJson.Default.EngineCommand)!;
        Assert.Equal("forbidden", f.Apply(fact, "s", 1).Error);
        Assert.Equal("pending", f.State("engine_step", "s"));
        Assert.Equal(1, f.Count("engine_event"));
    }

    [Fact]
    public void Cancellation_pushes_each_changed_entity_and_waits_for_each_worker()
    {
        using var f = new EngineFixture(); Create(f, Def("s"), Def("second"), Def("pending"));
        f.SeedSession("worker2", parentId: "owner", runId: "r");
        Assert.True(Start(f).Applied);
        Assert.True(f.Apply(new StartStep("worker2"), "second", 1).Applied);
        var before = f.Events().Count;
        Assert.True(f.Apply(new CancelRun(), "r", 2).Applied);
        var events = f.Events().Skip(before).ToArray();
        Assert.Contains(events, e => e.EntityId == "pending" && e.Data.State == "cancelled");
        Assert.Contains(events, e => e.EntityId == "worker" && e.Kind == "session.cancel_requested");
        Assert.Contains(events, e => e.EntityId == "worker2" && e.Kind == "session.cancel_requested");
        f.Db.Writer.Write("drain", s => s.Execute("UPDATE engine_outbox SET state='done'"));
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "worker", 1, actor: System).Applied);
        Assert.Equal("cancelling", f.State("engine_run", "r"));
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "worker2", 1, actor: System).Applied);
        Assert.Equal("cancelled", f.State("engine_run", "r"));
        Assert.Equal(2, f.Number("SELECT COUNT(*) FROM engine_step_attempt WHERE state='cancelled'"));
        Assert.Contains(f.Events(), e => e.EntityId == "s" && e.EntityKind == "step" && e.Data.State == "cancelled");
    }

}
