using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Projects;
using Assembly.Engine;
using Assembly.Engine.Reading;
using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Tests.Engine;
using Microsoft.Data.Sqlite;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class SessionProjectTests : IDisposable
{
    private readonly TopicSet _topics = new();
    private readonly EngineFixture _f;
    private readonly SessionStore _sessions;
    private readonly SessionSummaries _summaries;
    public SessionProjectTests()
    {
        _f = new(topics: _topics); _f.SeedSession();
        _summaries = SessionSummaries.Load(RowStore.Open(_f.Db));
        _sessions = SessionStore.Open(_f.Db, _f.Time, "/isolated/checkouts", _summaries);
        _sessions.Add("s", "codex", "plain", machine: null!, workingFolder: "/isolated/working");
    }
    public void Dispose() => _f.Dispose();
    private CommandContext Context(long version = 0, EngineActor? actor = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), _f.Machine, actor ?? new(ActorKind.System, "projects-test"), "s", version, 0, 1000);
    private CommandResult Apply(CommandContext context, string project, ISessionProjectProjection? projection = null) =>
        _f.Db.Writer.Write("test.project-command", scope => new EngineCommands(_f.Machine, _f.Limits,
            projectProjection: projection ?? _sessions).Apply(scope, context, new AssignProject(project)));
    [Fact]
    public void Initial_engine_assignment_commits_authority_and_projection_together()
    {
        var result = Apply(Context(), "p1");
        Assert.True(result.Applied); Assert.Equal("p1", _f.Text("SELECT project_id FROM engine_session WHERE id='s'"));
        var session = _sessions.Get("s")!;
        Assert.Equal("p1", session.ProjectId); Assert.Equal("p1", _sessions.Item(session).ProjectId);
        Assert.Equal("/isolated/working", session.WorkingFolder); Assert.Equal(1, _f.Count("engine_event"));
        var display = new EngineReader(_f.Db, _f.Machine).Entity("session", "s", default)!.Value;
        Assert.Equal("p1", display.GetProperty("projectId").GetString()); Assert.Equal("ready", display.GetProperty("checkoutState").GetString());
        var wire = JsonSerializer.SerializeToElement(_sessions.Item(session), SessionsJson.Default.SessionItem);
        Assert.Equal("p1", wire.GetProperty("projectId").GetString());
    }
    [Fact]
    public void A_missing_projector_cannot_commit_an_authority_only_assignment()
    {
        Assert.Throws<InvalidOperationException>(() => _f.Db.Writer.Write("test.missing-projector", scope =>
            new EngineCommands(_f.Machine, _f.Limits).Apply(scope, Context(), new AssignProject("p1"))));
        Assert.Null(_sessions.Get("s")!.ProjectId); Assert.Null(_f.Text("SELECT project_id FROM engine_session WHERE id='s'"));
        Assert.Equal(0, _f.Count("engine_event")); Assert.Equal(0, _f.Count("engine_command_receipt"));
    }
    [Fact]
    public void A_second_project_is_refused_and_the_same_project_is_idempotent()
    {
        Assert.True(Apply(Context(), "p1").Applied);
        var same = Apply(Context(1), "p1"); Assert.True(same.Applied); Assert.Equal(1, same.Version);
        Assert.False(Apply(Context(1), "p2").Applied); Assert.False(Apply(Context(1), "").Applied);
        Assert.Equal("p1", _sessions.Get("s")!.ProjectId); Assert.Equal(1, _f.Count("engine_event"));
    }
    [Theory]
    [InlineData(ActorKind.Window)]
    [InlineData(ActorKind.Session)]
    public void Only_system_actors_can_make_the_initial_assignment(ActorKind actor)
    {
        var result = Apply(Context(actor: new(actor, "s")), "p1");
        Assert.False(result.Applied); Assert.Equal("forbidden", result.Error);
        Assert.Null(_sessions.Get("s")!.ProjectId); Assert.Null(_f.Text("SELECT project_id FROM engine_session WHERE id='s'"));
        Assert.Equal(0, _f.Count("engine_event"));
    }
    [Fact]
    public void A_fault_after_projection_rolls_back_authority_projection_events_receipt_and_pushes()
    {
        var changes = new List<string>(); using var chat = _topics.Subscribe("sessions", null, _ => { }, (_, value) => changes.Add(value));
        using var engine = _topics.Subscribe("__engine_committed", null, _ => { }, (_, value) => changes.Add(value));
        Assert.Throws<InvalidOperationException>(() => Apply(Context(), "p1", new FaultAfterProjection(_sessions)));
        Assert.Null(_sessions.Get("s")!.ProjectId); Assert.Null(_f.Text("SELECT project_id FROM engine_session WHERE id='s'"));
        Assert.Equal(0, _f.Count("engine_event")); Assert.Equal(0, _f.Count("engine_command_receipt")); Assert.Empty(changes);
    }
    [Fact]
    public void Receipt_replay_precedes_current_version_and_projection_and_conflicting_reuse_is_refused()
    {
        var context = Context(); var accepted = Apply(context, "p1");
        Assert.Equal(accepted, Apply(context, "p1", new FaultAfterProjection(_sessions)));
        Assert.Equal("command_id_conflict", Apply(context, "p2").Error);
        Assert.Equal(1, _f.Count("engine_event")); Assert.Equal(1, _f.Count("engine_command_receipt"));
        Assert.Equal("p1", _sessions.Get("s")!.ProjectId);
    }
    [Fact]
    public void Assigned_projection_cannot_be_replaced_or_cleared_by_sql()
    {
        Apply(Context(), "p1");
        foreach (var replacement in new string?[] { "p2", null })
            Assert.Throws<SqliteException>(() => _f.Db.Writer.Write("test.project-guard", s =>
                s.Execute("UPDATE session_info SET project_id=$project WHERE session_id='s'", new SqlArg("$project", replacement))));
        Assert.Equal("p1", _sessions.Get("s")!.ProjectId);
    }
    [Fact]
    public void Checkout_projection_keeps_nullable_branch_and_the_original_working_folder()
    {
        _f.Db.Writer.Write("test.project-checkout", s => { _sessions.CheckoutChanged(s, "s", "/isolated/checkout", "main"); return 0; });
        _f.Db.Writer.Write("test.detached-checkout", s => { _sessions.CheckoutChanged(s, "s", "/isolated/checkout", null); return 0; });
        var session = _sessions.Get("s")!; var item = _sessions.Item(session);
        Assert.Equal("/isolated/checkout", item.CheckoutRoot); Assert.Null(item.Branch);
        Assert.Equal("/isolated/working", session.WorkingFolder);
    }
    [Fact]
    public void Client_ids_are_unique_while_null_legacy_ids_remain_valid()
    {
        _sessions.Add("s2", "codex", "plain", machine: null!);
        _f.Db.Writer.Write("test.client-id", s => s.Execute("UPDATE session_info SET client_id='same' WHERE session_id='s'"));
        Assert.Throws<SqliteException>(() => _f.Db.Writer.Write("test.duplicate-client-id", s => s.Execute("UPDATE session_info SET client_id='same' WHERE session_id='s2'")));
        Assert.Null(_f.Text("SELECT client_id FROM session_info WHERE session_id='s2'"));
    }
    [Fact]
    public void Only_if_empty_uses_the_real_project_projection_and_keeps_its_session()
    {
        var store = new ProjectStore(_f.Db, _f.Time);
        var project = _f.Db.Writer.Write("test.project-add", s => store.AddOrRestore(s, "local", "/isolated/working", "Project", null).Project);
        Apply(Context(), project.Id);
        Assert.Throws<CallFailed>(() => _f.Db.Writer.Write("test.project-remove", s => store.Remove(s, project.Id, true)));
        Assert.Null(store.Get(project.Id)!.RemovedAt); Assert.Equal(project.Id, _sessions.Get("s")!.ProjectId);
    }
    [Fact]
    public void A_legacy_remote_push_preserves_project_checkout_and_branch()
    {
        var current = RemoteWithCheckout();
        _sessions.ApplyRemote(current with { ProjectId = null, CheckoutRoot = null, Branch = null, Title = "Legacy title", UpdatedAt = 2000 });
        var session = _sessions.Get("remote")!;
        Assert.Equal("p1", session.ProjectId); Assert.Equal("/remote/checkout", session.CheckoutRoot); Assert.Equal("main", session.Branch);
        Assert.Equal("Legacy title", session.Title); Assert.Equal(2000, session.UpdatedAt);
    }
    [Fact]
    public void A_current_remote_checkout_can_report_detached_head()
    {
        var current = RemoteWithCheckout();
        _sessions.ApplyRemote(current with { CheckoutRoot = "/remote/other-checkout", Branch = null, UpdatedAt = 2000 });
        var session = _sessions.Get("remote")!;
        Assert.Equal("p1", session.ProjectId); Assert.Equal("/remote/other-checkout", session.CheckoutRoot); Assert.Null(session.Branch);
        Assert.Null(_sessions.Item(session).Branch); Assert.Equal(2000, session.UpdatedAt);
    }
    [Fact]
    public void A_conflicting_remote_project_rolls_back_title_time_checkout_summary_and_publication()
    {
        var current = RemoteWithCheckout();
        var before = _sessions.Get("remote")!; var summary = _summaries.Get("remote");
        Assert.Equal("kept-run", summary.RunId); Assert.True(summary.PendingAsk); Assert.Equal(2, summary.Background);
        var changes = new List<string>();
        using var sessions = _topics.Subscribe(SessionsFeature.Topic, null, _ => { }, (_, value) => changes.Add(value));
        using var rail = _topics.Subscribe(SessionsFeature.RailTopic, null, _ => { }, (_, value) => changes.Add(value));
        Assert.Throws<SqliteException>(() => _sessions.ApplyRemote(current with
        {
            ProjectId = "p2",
            CheckoutRoot = "/remote/refused",
            Branch = null,
            Title = "Refused title",
            TitleSource = "agent",
            UpdatedAt = 9000,
            ActiveRunId = null,
            State = "failed",
            PendingAsk = false,
            Background = 0
        }));
        Assert.Equal(before, _sessions.Get("remote")); Assert.Equal(summary, _summaries.Get("remote"));
        Assert.Equal(current, _sessions.Item(_sessions.Get("remote")!)); Assert.Empty(changes);
    }
    private SessionItem RemoteWithCheckout()
    {
        var session = _sessions.Add("remote", "codex", "plain", "workbox-test");
        _sessions.ApplyRemote(_sessions.Item(session) with
        {
            ProjectId = "p1",
            CheckoutRoot = "/remote/checkout",
            Branch = "main",
            Title = "Kept title",
            UpdatedAt = 1500,
            ActiveRunId = "kept-run",
            PendingAsk = true,
            Background = 2
        });
        return _sessions.Item(_sessions.Get("remote")!);
    }
    private sealed class FaultAfterProjection(SessionStore sessions) : ISessionProjectProjection
    {
        public void Assigned(IWriteScope scope, string id, string? project)
        { sessions.Assigned(scope, id, project); throw new InvalidOperationException("injected after projection"); }
        public void CheckoutChanged(IWriteScope scope, string id, string folder, string? branch) => sessions.CheckoutChanged(scope, id, folder, branch);
    }
}
