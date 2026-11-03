using Assembly.Engine.Contracts;
using Assembly.Engine.Prs;

namespace Assembly.Tests.Engine;

public sealed class PrObservationTests
{
    private static readonly EngineActor Owner = new(ActorKind.Session, "owner");
    private static readonly EngineActor Observer = new(ActorKind.System, "github-observer");
    private static ReportPr Report(string? run, int number, string head, string? owner = null) =>
        new(run, "org/repo", number, head, $"https://github.com/org/repo/pull/{number}", "open", OwnerSessionId: owner);
    [Fact]
    public void Multiple_run_prs_and_runless_owner_prs_remain_distinct_and_primary_is_first_only()
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedSession("standalone"); f.SeedRun("run", "owner");
        Assert.True(f.Apply(Report("run", 1, "first"), "pr-first", actor: Owner).Applied);
        Assert.True(f.Apply(Report("run", 2, "second"), "pr-second", actor: Owner).Applied);
        Assert.True(f.Apply(Report(null, 3, "standalone-head", "standalone"), "pr-standalone", actor: new(ActorKind.Session, "standalone")).Applied);
        Assert.Equal(2, f.Number("SELECT COUNT(*) FROM engine_pr WHERE run_id='run'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_pr WHERE run_id IS NULL AND owner_session_id='standalone'"));
        Assert.Equal("pr-first", f.Text("SELECT pr_id FROM engine_run WHERE id='run'"));
        Assert.Equal("standalone", Assert.Single(f.Events(), item => item.EntityKind == "pr" && item.EntityId == "pr-standalone").Data.SessionId);
        f.Reopen(); Assert.Equal(3, f.Count("engine_pr"));
    }
    [Fact]
    public void A_session_cannot_report_another_sessions_runless_pr()
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedSession("other");
        var refused = f.Apply(Report(null, 1, "head", "owner"), "pr", actor: new(ActorKind.Session, "other"));
        Assert.Equal("forbidden", refused.Error); Assert.Equal(0, f.Count("engine_pr"));
    }
    [Fact]
    public void A_newer_trusted_changed_head_is_observed_without_turning_agent_claims_into_merge_proof()
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedRun("run", "owner");
        Assert.True(f.Apply(Report("run", 1, "reported-head") with { ReportedState = "merged" }, "pr", actor: Owner).Applied);
        Assert.Equal("not_created", f.State("engine_pr", "pr"));
        Assert.Equal("forbidden", f.Apply(new ObservePr("merged", "changed-head", 1000), "pr", 1, actor: Owner).Error);
        Assert.True(f.Apply(new ObservePr("open", "changed-head", 1000), "pr", 1, actor: Observer).Applied);
        Assert.Equal("reported-head", f.Text("SELECT head FROM engine_pr WHERE id='pr'"));
        Assert.Equal("changed-head", f.Text("SELECT observed_head FROM engine_pr WHERE id='pr'"));
        Assert.True(f.Apply(new ObservePr("merged", "changed-head", 2000), "pr", 2, actor: Observer).Applied);
        var events = f.Count("engine_event");
        var stale = f.Apply(new ObservePr("open", "reported-head", 1500), "pr", 1, actor: Observer);
        Assert.False(stale.Applied); Assert.Null(stale.Error); Assert.Equal(events, f.Count("engine_event"));
        Assert.Equal("merged", f.State("engine_pr", "pr"));
    }

    [Fact]
    public void Local_trusted_fact_intent_and_pr_are_committed_once_and_survive_restart()
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedRun("run", "owner");
        f.Db.Writer.Write("test.actual-task", scope => scope.Execute("UPDATE engine_run SET task_url='https://app.notion.com/p/actual-task' WHERE id='run'"));
        var id = Guid.NewGuid().ToString("N"); var commandId = Guid.NewGuid();
        var facts = new PrObservationFacts("owner", "run", null, 0, "org/repo", 9, "actual-head", "https://github.com/org/repo/pull/9", "open", 1000);
        var first = f.Apply(new RecordPrObservation(f.Machine, facts), id, actor: Observer, commandId: commandId);
        Assert.True(first.Applied); Assert.True(first.Seq > 0);
        Assert.Equal("recorded", f.State("engine_pr_observation", id));
        Assert.Equal("open", f.State("engine_pr", id));
        Assert.Equal("https://app.notion.com/p/actual-task", f.Text("SELECT task_url FROM engine_pr WHERE id='" + id + "'"));
        Assert.Null(f.Text("SELECT reported_by FROM engine_pr WHERE id='" + id + "'"));
        var events = f.Count("engine_event");
        Assert.Equal(first, f.Apply(new RecordPrObservation(f.Machine, facts), id, actor: Observer, commandId: commandId));
        var replay = f.Apply(new RecordPrObservation(f.Machine, facts), id, version: 2, actor: Observer);
        Assert.Equal(first, replay);
        Assert.Equal(events, f.Count("engine_event")); f.Reopen();
        Assert.Equal(1, f.Count("engine_pr")); Assert.Equal(1, f.Count("engine_pr_observation"));
        Assert.Equal(first.Seq, f.Number("SELECT coordinator_seq FROM engine_pr_observation WHERE id='" + id + "'"));
    }
    [Fact]
    public void Untrusted_or_stale_source_facts_write_neither_pr_nor_intent()
    {
        using var f = new EngineFixture(); f.SeedSession("owner");
        var facts = new PrObservationFacts("owner", null, null, 0, "org/repo", 9, "head", "https://github.com/org/repo/pull/9", "merged", 1000);
        Assert.Equal("forbidden", f.Apply(new RecordPrObservation(f.Machine, facts), Guid.NewGuid().ToString("N"), actor: Owner).Error);
        Assert.Equal("stale_epoch", f.Apply(new RecordPrObservation(f.Machine, facts with { SessionEpoch = 1 }), Guid.NewGuid().ToString("N"), epoch: 1, actor: Observer).Error);
        Assert.Equal(0, f.Count("engine_pr")); Assert.Equal(0, f.Count("engine_pr_observation"));
    }
    [Fact]
    public void Fault_in_observation_journal_rolls_back_intent_association_primary_and_receipt()
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedRun("run", "owner");
        f.Db.Writer.Write("test.reject-observation", s => s.Execute("CREATE TRIGGER reject_observation BEFORE INSERT ON engine_event BEGIN SELECT RAISE(ABORT,'injected'); END"));
        var facts = new PrObservationFacts("owner", "run", null, 0, "org/repo", 9, "head", "https://github.com/org/repo/pull/9", "open", 1000);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => f.Apply(new RecordPrObservation(f.Machine, facts), Guid.NewGuid().ToString("N"), actor: Observer));
        Assert.Equal(0, f.Count("engine_pr")); Assert.Equal(0, f.Count("engine_pr_observation")); Assert.Equal(0, f.Count("engine_command_receipt"));
        Assert.Null(f.Text("SELECT pr_id FROM engine_run WHERE id='run'"));
    }

    [Fact]
    public void Existing_report_bytes_remain_unchanged_and_new_fact_commands_round_trip()
    {
        const string old = "{\"kind\":\"report_pr\",\"runId\":\"r\",\"repo\":\"org/repo\",\"number\":1,\"head\":\"head\",\"url\":\"https://github.com/org/repo/pull/1\",\"reportedState\":\"open\"}";
        var report = new ReportPr("r", "org/repo", 1, "head", "https://github.com/org/repo/pull/1", "open");
        Assert.Equal(old, System.Text.Json.JsonSerializer.Serialize<EngineCommand>(report, Assembly.Engine.EngineJson.Default.EngineCommand));
        Assert.Equal(report, System.Text.Json.JsonSerializer.Deserialize(old, Assembly.Engine.EngineJson.Default.EngineCommand));
        var facts = new PrObservationFacts("owner", null, null, 0, "org/repo", 1, "head", "https://github.com/org/repo/pull/1", "open", 1000);
        EngineCommand[] commands = [new RecordPrObservation("machine", facts), new ApplyPrObservation("origin", Guid.NewGuid().ToString("N"), facts), new PrObservationRecorded(Guid.NewGuid().ToString("N"), "coordinator", 12)];
        foreach (var command in commands)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(command, Assembly.Engine.EngineJson.Default.EngineCommand);
            Assert.Equal(command, System.Text.Json.JsonSerializer.Deserialize(json, Assembly.Engine.EngineJson.Default.EngineCommand));
        }
    }
}
