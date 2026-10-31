using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Prs;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine;

public sealed class PrTests
{
    private const string Link = "https://github.com/org/repo/pull/42";
    private const string TaskUrl = "https://app.notion.com/p/task";
    private static readonly EngineActor Owner = new(ActorKind.Session, "owner-session");
    private static readonly EngineActor Observer = new(ActorKind.System, "observer");
    private static ReportPr Report(string state = "open") => new("r", "org/repo", 42, "abc", Link, state);

    private static EngineFixture Fixture()
    {
        var f = new EngineFixture();
        f.SeedSession("owner-session");
        f.SeedRun("r", ownerSessionId: "owner-session");
        f.Db.Writer.Write("test.task-url", s => s.Execute("UPDATE engine_run SET task_url=$url WHERE id='r'", new SqlArg("$url", TaskUrl)));
        return f;
    }

    [Fact]
    public void Agent_claim_cannot_merge()
    {
        using var f = Fixture();
        Assert.True(f.Apply(Report("merged"), "p", actor: Owner).Applied);
        Assert.Equal("not_created", f.State("engine_pr", "p"));
        var r = f.Apply(new ObservePr("merged", "old-head", 1000), "p", version: 1, actor: Observer);
        Assert.Equal("head_mismatch", r.Error);
        Assert.Equal("p", f.Text("SELECT pr_id FROM engine_run WHERE id='r'"));
        Assert.Equal("owner-session", f.Text("SELECT reported_by FROM engine_pr WHERE id='p'"));
        Assert.Equal("merged", f.Text("SELECT reported_state FROM engine_pr WHERE id='p'"));
        Assert.Equal(2, f.Count("engine_event"));
        Assert.Equal(1, f.Number("SELECT version FROM engine_run WHERE id='r'"));
        Assert.Null(f.Text("SELECT observed_head FROM engine_pr WHERE id='p'"));
    }

    [Fact]
    public void Observed_head_and_time_pin_state()
    {
        using var f = Fixture();
        Assert.True(f.Apply(Report(), "p", actor: Owner).Applied);
        Assert.True(f.Apply(new ObservePr("open", "abc", 1000), "p", 1, actor: Observer).Applied);
        Assert.True(f.Apply(new ObservePr("merged", "abc", 2000), "p", 2, actor: Observer).Applied);
        var events = f.Count("engine_event");
        // Even an outdated expected version cannot turn an old observation into an error.
        var older = f.Apply(new ObservePr("open", "abc", 1500), "p", 1, actor: Observer);
        Assert.False(older.Applied);
        Assert.Null(older.Error);
        Assert.Equal(3, older.Version);
        Assert.Equal(events, f.Count("engine_event"));
        Assert.True(f.Apply(Report("open"), "p", 3, actor: Owner).Applied);
        Assert.Equal("merged", f.State("engine_pr", "p"));
        Assert.Equal("abc", f.Text("SELECT observed_head FROM engine_pr WHERE id='p'"));
        Assert.Equal(2000, f.Number("SELECT observed_ms FROM engine_pr WHERE id='p'"));
        Assert.Equal(TaskUrl, f.Text("SELECT task_url FROM engine_pr WHERE id='p'"));
        Assert.Equal(TaskUrl, f.Text("SELECT task_url FROM engine_run WHERE id='r'"));
        Assert.Equal(Link, f.Text("SELECT url FROM engine_pr WHERE id='p'"));
        f.Reopen();
        Assert.Equal("merged", f.State("engine_pr", "p"));
    }

    [Fact]
    public void Terminal_pr_keeps_its_identity()
    {
        using var f = Fixture();
        Assert.True(f.Apply(Report(), "p", actor: Owner).Applied);
        Assert.True(f.Apply(new ObservePr("open", "abc", 1000), "p", 1, actor: Observer).Applied);
        Assert.True(f.Apply(new ObservePr("merged", "abc", 2000), "p", 2, actor: Observer).Applied);
        var events = f.Count("engine_event");
        ReportPr[] changedIdentities =
        [
            Report() with { Head = "def" },
            Report() with { Repo = "org/other" },
            Report() with { Number = 43 }
        ];
        foreach (var report in changedIdentities)
        {
            var refused = f.Apply(report, "p", 3, actor: Owner);
            Assert.False(refused.Applied);
            Assert.Equal("invalid_transition", refused.Error);
            Assert.Equal(3, refused.Version);
            Assert.Equal(0, refused.Epoch);
            Assert.Equal(events, f.Count("engine_event"));
            Assert.Equal("abc", f.Text("SELECT head FROM engine_pr WHERE id='p'"));
            Assert.Equal("org/repo", f.Text("SELECT repo FROM engine_pr WHERE id='p'"));
            Assert.Equal(42, f.Number("SELECT number FROM engine_pr WHERE id='p'"));
            Assert.Equal(3, f.Number("SELECT version FROM engine_pr WHERE id='p'"));
        }
        const string updatedUrl = "https://github.com/org/repo/pull/42?updated";
        var accepted = f.Apply(Report("closed") with { Url = updatedUrl }, "p", 3,
            actor: new(ActorKind.Window, "reporter"));
        Assert.True(accepted.Applied);
        Assert.Equal(4, accepted.Version);
        Assert.Equal(events + 1, f.Count("engine_event"));
        Assert.Equal("merged", f.State("engine_pr", "p"));
        Assert.Equal("abc", f.Text("SELECT head FROM engine_pr WHERE id='p'"));
        Assert.Equal("abc", f.Text("SELECT observed_head FROM engine_pr WHERE id='p'"));
        Assert.Equal(2000, f.Number("SELECT observed_ms FROM engine_pr WHERE id='p'"));
        Assert.Equal("reporter", f.Text("SELECT reported_by FROM engine_pr WHERE id='p'"));
        Assert.Equal("closed", f.Text("SELECT reported_state FROM engine_pr WHERE id='p'"));
        Assert.Equal(updatedUrl, f.Text("SELECT url FROM engine_pr WHERE id='p'"));
    }

    [Theory]
    [InlineData(ActorKind.Session)]
    [InlineData(ActorKind.Window)]
    public void Only_system_can_observe(ActorKind kind)
    {
        using var f = Fixture();
        f.Apply(Report(), "p", actor: Owner);
        var events = f.Count("engine_event");
        Assert.Equal("forbidden", f.Apply(new ObservePr("open", "abc", 1000), "p", 1, actor: new(kind, "owner-session")).Error);
        Assert.Equal(events, f.Count("engine_event"));
        Assert.Equal("not_created", f.State("engine_pr", "p"));
    }

    [Theory]
    [InlineData("merged")]
    [InlineData("closed")]
    [InlineData("unknown")]
    public void Observations_control_transitions_and_terminal_states_refuse_changes(string state)
    {
        using var f = Fixture();
        f.Apply(Report(), "p", actor: Owner);
        Assert.True(f.Apply(new ObservePr("open", "abc", 1000), "p", 1, actor: Observer).Applied);
        Assert.True(f.Apply(new ObservePr(state, "abc", 2000), "p", 2, actor: Observer).Applied);
        Assert.Equal(state, f.State("engine_pr", "p"));
        var events = f.Count("engine_event");
        var next = f.Apply(new ObservePr("unknown", "abc", 3000), "p", 3, actor: Observer);
        if (state == "unknown") Assert.True(next.Applied);
        else
        {
            Assert.Equal("invalid_transition", next.Error);
            Assert.Equal(events, f.Count("engine_event"));
            Assert.Equal(2000, f.Number("SELECT observed_ms FROM engine_pr WHERE id='p'"));
        }
    }

    [Fact]
    public void Unknown_is_allowed_before_open_and_can_recover()
    {
        using var f = Fixture();
        f.Apply(Report(), "p", actor: Owner);
        Assert.True(f.Apply(new ObservePr("unknown", "abc", 1000), "p", 1, actor: Observer).Applied);
        Assert.True(f.Apply(new ObservePr("open", "abc", 2000), "p", 2, actor: Observer).Applied);
    }

    [Theory]
    [InlineData(ActorKind.Session, "other-session", false)]
    [InlineData(ActorKind.System, "observer", false)]
    [InlineData(ActorKind.Window, "window", true)]
    public void Reports_require_run_owner_or_window(ActorKind kind, string id, bool allowed)
    {
        using var f = Fixture();
        var result = f.Apply(Report(), "p", actor: new(kind, id));
        Assert.Equal(allowed, result.Applied);
        if (allowed) return;
        Assert.Equal("forbidden", result.Error);
        Assert.Equal(0, f.Count("engine_pr"));
        Assert.Equal(0, f.Count("engine_event"));
        Assert.Null(f.Text("SELECT pr_id FROM engine_run WHERE id='r'"));
    }

    [Theory]
    [InlineData("", 42, Link)]
    [InlineData("org/repo", 0, Link)]
    [InlineData("org/repo", -1, Link)]
    [InlineData("org/repo", 42, "http://github.com/org/repo/pull/42")]
    [InlineData("org/repo", 42, "invalid")]
    public void Invalid_identity_is_rejected_without_events(string repo, int number, string url)
    {
        using var f = Fixture();
        Assert.Equal("bad_args", f.Apply(new ReportPr("r", repo, number, "abc", url, "open"), "p", actor: Owner).Error);
        Assert.Equal(0, f.Count("engine_event"));
        Assert.Equal(0, f.Count("engine_pr"));
    }

    [Fact]
    public void Identity_is_unique_and_conflicts_do_not_relink_runs()
    {
        using var f = Fixture();
        f.Apply(Report(), "p", actor: Owner);
        f.SeedRun("second-run", ownerSessionId: "owner-session");
        var events = f.Count("engine_event");
        Assert.Equal("bad_args", f.Apply(Report() with { RunId = "second-run" }, "other-pr", actor: Owner).Error);
        Assert.Equal(1, f.Count("engine_pr"));
        Assert.Null(f.Text("SELECT pr_id FROM engine_run WHERE id='second-run'"));
        Assert.Equal(events, f.Count("engine_event"));
        Assert.Equal("p", f.Text("SELECT pr_id FROM engine_run WHERE id='r'"));
    }

    [Fact]
    public void Version_epoch_and_invalid_observation_leave_state_unchanged()
    {
        using var f = Fixture();
        f.Apply(Report(), "p", actor: Owner);
        var events = f.Count("engine_event");
        Assert.Equal("version_conflict", f.Apply(Report(), "p", 0, actor: Owner).Error);
        Assert.Equal("stale_epoch", f.Apply(Report(), "p", 1, epoch: 1, actor: Owner).Error);
        Assert.Equal("version_conflict", f.Apply(new ObservePr("open", "abc", 1000), "p", 0, actor: Observer).Error);
        Assert.Equal("invalid_transition", f.Apply(new ObservePr("invented", "abc", 1000), "p", 1, actor: Observer).Error);
        Assert.Equal(events, f.Count("engine_event"));
        Assert.Equal("not_created", f.State("engine_pr", "p"));
    }

    [Fact]
    public void Report_and_run_link_roll_back_together_if_journal_fails()
    {
        using var f = Fixture();
        f.Db.Writer.Write("test.fail-journal", s => s.Execute("CREATE TRIGGER fail_pr_event AFTER INSERT ON engine_event BEGIN SELECT RAISE(ABORT,'injected'); END"));
        Assert.Throws<SqliteException>(() => f.Apply(Report(), "p", actor: Owner));
        Assert.Equal(0, f.Count("engine_pr"));
        Assert.Equal(0, f.Count("engine_event"));
        Assert.Equal(0, f.Count("engine_command_receipt"));
        Assert.Null(f.Text("SELECT pr_id FROM engine_run WHERE id='r'"));
        Assert.Equal(0, f.Number("SELECT version FROM engine_run WHERE id='r'"));
    }

    [Fact]
    public void Report_cannot_move_a_pr_to_another_run()
    {
        using var f = Fixture();
        f.Apply(Report(), "p", actor: Owner);
        f.SeedRun("other-run", ownerSessionId: "owner-session");
        var events = f.Count("engine_event");
        Assert.Equal("bad_args", f.Apply(Report() with { RunId = "other-run" }, "p", 1, actor: Owner).Error);
        Assert.Equal(events, f.Count("engine_event"));
        Assert.Null(f.Text("SELECT pr_id FROM engine_run WHERE id='other-run'"));
        Assert.Equal("r", f.Text("SELECT run_id FROM engine_pr WHERE id='p'"));
    }

    [Fact]
    public void Commands_round_trip_and_replays_do_not_duplicate_events()
    {
        using var f = Fixture();
        EngineCommand[] commands = [Report(), new ObservePr("open", "abc", 1000)];
        foreach (var command in commands)
        {
            var json = JsonSerializer.Serialize(command, EngineJson.Default.EngineCommand);
            Assert.Equal(command, JsonSerializer.Deserialize(json, EngineJson.Default.EngineCommand));
        }
        var commandId = Guid.NewGuid();
        var first = f.Apply(Report(), "p", actor: Owner, commandId: commandId);
        Assert.Equal(first, f.Apply(Report(), "p", actor: Owner, commandId: commandId));
        Assert.Equal(2, f.Count("engine_event"));
    }
}
