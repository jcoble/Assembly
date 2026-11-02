using System.Collections.Immutable;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Runs;
using Assembly.Infrastructure.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Workflows.Runs;

public sealed class WorkflowStartTests
{
    [Fact]
    public void Ten_tasks_create_ten_runs_and_distinct_mapped_graphs_under_one_owner()
    {
        using var f = new Fixture();
        for (var i = 0; i < 10; i++)
        {
            var request = f.Request("key-" + i) with { Task = Json($$"""{"key":"TSK-{{i}}","title":"Task {{i}}","body":"body","url":"https://example.com/tasks/{{i}}"}""") };
            Assert.True(f.Apply(f.Prepare(request)).Result.Applied);
        }
        Assert.Equal(10, f.Core.Count("engine_run")); Assert.Equal(20, f.Core.Count("engine_step"));
        Assert.Equal(10, f.Core.Number("SELECT COUNT(*) FROM engine_step_dependency"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(DISTINCT owner_session_id) FROM engine_run"));
        f.Core.Reopen();
        Assert.Equal(10, f.Core.Number("SELECT COUNT(*) FROM engine_step WHERE definition_key='build'"));
        Assert.Equal(10, f.Core.Number("SELECT COUNT(*) FROM engine_step WHERE definition_key='checks'"));
        Assert.Equal(10, f.Core.Count("engine_workflow_snapshot")); Assert.Equal(10, f.Core.Count("engine_workflow_history"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("https://example.com/task", true)]
    [InlineData("http://example.com/task", false)]
    public void Task_url_is_optional_but_supplied_url_must_be_https(string? url, bool accepted)
    {
        using var f = new Fixture();
        var task = url is null ? "{\"key\":\"TSK-1\",\"title\":\"Task\",\"body\":\"body\"}" :
            "{\"key\":\"TSK-1\",\"title\":\"Task\",\"body\":\"body\",\"url\":" + JsonSerializer.Serialize(url) + "}";
        var request = f.Request() with { Task = Json(task) };
        var ack = f.Apply(f.Prepare(request));
        Assert.Equal(accepted, ack.Result.Applied);
        if (accepted) Assert.Equal(url, f.Core.Text("SELECT task_url FROM engine_run"));
        else { Assert.Equal("bad_args", ack.Result.Error); Assert.Equal(0, f.Core.Count("engine_run")); }
    }

    [Fact]
    public void Freeform_run_has_null_task_request_title_and_pending_setup_intent()
    {
        using var f = new Fixture(); var request = f.Request(); var ack = f.Apply(f.Prepare(request));
        Assert.True(ack.Result.Applied, ack.Result.Error); Assert.Equal(request.ProposedRunId, ack.RunId);
        Assert.Null(f.Core.Text("SELECT task_url FROM engine_run"));
        Assert.Equal("fix this bug", f.Core.Text("SELECT title FROM engine_run"));
        Assert.Equal("pending", f.Core.Text("SELECT setup_state FROM engine_run"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_setup' AND state='pending'"));
        Assert.Equal(0, f.Core.Count("engine_workflow_receipt"));
    }

    [Fact]
    public void Existing_key_prepares_before_rereading_a_deleted_definition_and_returns_original_after_reopen()
    {
        var folder = Path.Combine(Path.GetTempPath(), "a40-workflow-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, ".assembly", "workflows"));
            using var f = new Fixture(folder);
            var yaml = Path.Combine(folder, ".assembly", "workflows", "flow.yml");
            File.WriteAllText(yaml, Definition); File.WriteAllText(Path.Combine(folder, ".assembly", "workflows", "prompt.md"), "original prompt");
            var request = f.Request(); var prepared = f.Prepare(request, WorkflowLoader.Load(folder, request.Path));
            var first = f.Apply(prepared); Assert.True(first.Result.Applied, first.Result.Error);
            File.Delete(yaml); f.Core.Reopen();
            using var snapshot = f.Core.Db.OpenSnapshot(default);
            var retry = WorkflowStarts.PrepareExisting(snapshot, prepared.Start.Context, request, f.Project);
            Assert.NotNull(retry); Assert.Equal(first, f.Apply(retry));
            Assert.Equal(1, f.Core.Count("engine_run")); Assert.Equal(1, f.Core.Count("engine_outbox"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void Same_key_different_request_conflicts_and_same_command_changed_body_conflicts()
    {
        using var f = new Fixture(); var request = f.Request(); var original = f.Prepare(request);
        Assert.True(f.Apply(original).Result.Applied);
        var other = request with { CommandId = Guid.NewGuid(), ProposedRunId = Guid.NewGuid().ToString("N"), Request = "different" };
        using var snapshot = f.Core.Db.OpenSnapshot(default);
        var differentKeyBody = WorkflowStarts.PrepareExisting(snapshot, f.Context(other), other, f.Project)!;
        Assert.Equal("request_key_conflict", f.Apply(differentKeyBody).Result.Error);
        var sameCommand = request with { Request = "changed" };
        Assert.Equal("command_id_conflict", f.Apply(WorkflowStarts.PrepareExisting(snapshot, f.Context(sameCommand), sameCommand, f.Project)!).Result.Error);
        Assert.Equal(1, f.Core.Count("engine_run"));
    }

    [Fact]
    public void Two_prepared_same_key_requests_converge_on_first_run_and_original_journal_result()
    {
        using var f = new Fixture(); var firstRequest = f.Request(); var secondRequest = f.Request();
        var first = f.Prepare(firstRequest); var second = f.Prepare(secondRequest);
        var a = f.Apply(first); var b = f.Apply(second);
        Assert.True(a.Result.Applied); Assert.Equal(a, b); Assert.Equal(firstRequest.ProposedRunId, b.RunId);
        Assert.Equal(1, f.Core.Count("engine_run")); Assert.Equal(2, f.Core.Count("engine_command_receipt"));
        Assert.Equal(2, f.Core.Count("engine_step"));
    }

    [Theory]
    [InlineData(false, "bad_args")]
    [InlineData(true, "owner_reserved")]
    public void Missing_selected_owner_or_child_owner_cannot_start(bool child, string expected)
    {
        using var f = new Fixture();
        if (child) f.Core.SeedSession("child", parentId: "owner");
        var request = f.Request() with { OwnerSessionId = child ? "child" : "" };
        Assert.Equal(expected, f.Apply(f.Prepare(request)).Result.Error);
        Assert.Equal(0, f.Core.Count("engine_run")); Assert.Equal(0, f.Core.Count("engine_outbox"));
    }

    [Fact]
    public void Snapshot_failure_rolls_back_graph_receipt_history_setup_and_publication_sequence()
    {
        using var f = new Fixture(); var prepared = f.Prepare(f.Request());
        var before = f.Core.Number("SELECT next_seq FROM engine_meta");
        f.Core.Db.Writer.Write("test.reject-snapshot", scope => scope.Execute("""
            CREATE TRIGGER reject_snapshot BEFORE INSERT ON engine_workflow_snapshot
            BEGIN SELECT RAISE(ABORT,'snapshot fault'); END;
            """));
        Assert.Throws<SqliteException>(() => f.Apply(prepared));
        foreach (var table in new[] { "engine_run", "engine_step", "engine_workflow_snapshot", "engine_workflow_start", "engine_workflow_history", "engine_outbox", "engine_command_receipt", "engine_event" }) Assert.Equal(0, f.Core.Count(table));
        Assert.Equal(before, f.Core.Number("SELECT next_seq FROM engine_meta"));
    }

    [Fact]
    public void Required_input_failure_creates_no_workflow_state()
    {
        using var f = new Fixture();
        var source = Definition.Replace("name: Flow", "name: Flow\ninputs:\n  required_value: {required: true}");
        var snapshot = WorkflowParser.Parse(new(source, Prompts("prompt")));
        Assert.Throws<WorkflowFieldException>(() => f.Prepare(f.Request(), snapshot));
        Assert.Equal(0, f.Core.Count("engine_run")); Assert.Equal(0, f.Core.Count("engine_step"));
    }

    [Fact]
    public void Large_full_prompt_is_private_and_journal_remains_bounded()
    {
        using var f = new Fixture(); var text = new string('x', 40000) + "\0🙂tail";
        var snapshot = WorkflowParser.Parse(new(Definition, Prompts(text)));
        var request = f.Request(); var prepared = f.Prepare(request, snapshot);
        Assert.True(prepared.BodyBytes > 32768); var ack = f.Apply(prepared); Assert.True(ack.Result.Applied, ack.Result.Error);
        using var read = f.Core.Db.OpenSnapshot(default);
        var replay = WorkflowStarts.PrepareExisting(read, f.Context(request), request, f.Project)!;
        Assert.Equal(text, replay.Start.Snapshot.Prompts["prompt.md"]); Assert.Equal(ack, f.Apply(replay));
        Assert.True(prepared.CanonicalJournal.Length < 512);
        Assert.DoesNotContain("xxxxx", f.Core.Text("SELECT data_json FROM engine_event")!);
    }

    [Fact]
    public void Explicit_optional_snapshot_step_can_rule_skip_but_required_step_cannot()
    {
        using var f = new Fixture();
        var definition = Definition.Replace("checks:", "checks:\n    required: false");
        var prepared = f.Prepare(f.Request(), WorkflowParser.Parse(new(definition, Prompts("prompt"))));
        Assert.True(f.Apply(prepared).Result.Applied);
        var required = prepared.Start.StepIds["build"]; var optional = prepared.Start.StepIds["checks"];
        var actor = new EngineActor(ActorKind.System, "workflow");
        Assert.Equal("forbidden", f.Core.Apply(new global::Assembly.Engine.Runs.SetStepState("skipped", 0, [], "optional", true), required, 1, actor: actor).Error);
        Assert.True(f.Core.Apply(new global::Assembly.Engine.Runs.SetStepState("skipped", 0, [], "optional", true), optional, 1, actor: actor).Applied);
        Assert.Equal("pending", f.Core.State("engine_step", required));
        Assert.Equal("skipped", f.Core.State("engine_step", optional));
    }

    private static JsonElement Json(string text) { using var value = JsonDocument.Parse(text); return value.RootElement.Clone(); }
    private static ImmutableSortedDictionary<string, string> Prompts(string text) => ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal).Add("prompt.md", text);
    private const string Definition = """
        name: Flow
        steps:
          build:
            agent: {provider: claude}
            prompt: prompt.md
            result: build
          checks:
            needs: [build]
            run: ["printf ok"]
        """;

    private sealed class Fixture : IDisposable
    {
        public EngineFixture Core { get; } = new();
        public ProjectRecord Project { get; }
        public Fixture(string root = "/test")
        {
            Core.SeedSession("owner"); var store = new ProjectStore(Core.Db, Core.Time);
            Project = Core.Db.Writer.Write("test.add-workflow-project", s => store.AddOrRestore(s, Core.Machine, root, "Test", null).Project);
        }
        public WorkflowStartRequest Request(string key = "key") => new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), 0, 0,
            "owner", key, ".assembly/workflows/flow.yml", Project.Id, "fix this bug", null, Json("{}"));
        public CommandContext Context(WorkflowStartRequest request) => new(request.CommandId, Core.Machine,
            new(ActorKind.Window, "owner"), request.ProposedRunId, request.ExpectedVersion, request.Epoch, 1000);
        public PreparedWorkflowCommand Prepare(WorkflowStartRequest request, WorkflowSnapshot? snapshot = null) =>
            WorkflowStarts.Prepare(Context(request), request, snapshot ?? WorkflowParser.Parse(new(Definition, Prompts("prompt"))), Project);
        public WorkflowStartAck Apply(PreparedWorkflowCommand prepared) => Core.Db.Writer.Write("workflow.start", scope =>
            WorkflowStarts.Apply(scope, new EngineCommands(Core.Machine, Core.Limits), prepared.Start.Context with { NowMs = 1000 }, prepared));
        public void Dispose() => Core.Dispose();
    }
}
