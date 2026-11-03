using System.Security.Claims;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Workflows;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowToolTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task Root_starts_freeform_workflow_through_real_tool_and_every_call_is_recorded(string provider)
    {
        await using var h = new Harness(provider);
        var format = await h.Call(new WorkflowFormatTool(h.Gate), "{}"); Assert.False(format.IsError);
        Assert.Equal(1, Json(format).GetProperty("schemaVersion").GetInt32());
        var checkedWorkflow = await h.Call(h.Check, "{\"path\":\".assembly/workflows/flow.yml\"}"); Assert.False(checkedWorkflow.IsError);
        Assert.True(Json(checkedWorkflow).GetProperty("ok").GetBoolean()); Assert.Equal(0, h.Fixture.Count("engine_run"));
        var started = await h.Call(h.Start, Start); Assert.False(started.IsError);
        var run = Json(started).GetProperty("runId").GetString();
        Assert.Equal("owner", h.Fixture.Text("SELECT owner_session_id FROM engine_run"));
        Assert.Equal("Fix the bug", h.Fixture.Text("SELECT request FROM engine_run"));
        Assert.Equal(Json(checkedWorkflow).GetProperty("definitionHash").GetString(), Json(started).GetProperty("definitionHash").GetString());
        Assert.Equal("pending", h.Fixture.Text("SELECT state FROM engine_step WHERE run_id=$run", new SqlArg("$run", run)));
        Assert.Equal(3, h.Fixture.Number("SELECT COUNT(*) FROM engine_tool_call WHERE session_id='owner' AND outcome='ok'"));
        Assert.Equal(1, h.Fixture.Count("engine_workflow_snapshot"));
    }

    [Fact]
    public async Task Same_request_key_replays_original_run_without_reloading_deleted_source_and_changed_body_conflicts()
    {
        await using var h = new Harness();
        var first = await h.Call(h.Start, Start); Assert.False(first.IsError);
        File.Delete(h.WorkflowPath);
        var replay = await h.Call(h.Start, Start); Assert.False(replay.IsError);
        Assert.Equal(Json(first).GetProperty("runId").GetString(), Json(replay).GetProperty("runId").GetString());
        Assert.Equal(1, h.Loads); Assert.Equal(1, h.Fixture.Count("engine_run"));
        var conflict = await h.Call(h.Start, Start.Replace("Fix the bug", "Different request"));
        Assert.True(conflict.IsError); Assert.Contains("request_key_conflict", Text(conflict)); Assert.Equal(1, h.Loads);
    }

    [Theory]
    [InlineData("ownerSessionId", "\"victim\"")]
    [InlineData("sessionId", "\"victim\"")]
    [InlineData("epoch", "99")]
    [InlineData("stepId", "\"other\"")]
    [InlineData("machine", "\"remote\"")]
    public async Task Strict_start_arguments_cannot_spoof_authority(string key, string value)
    {
        await using var h = new Harness(); var args = Start[..^1] + ",\"" + key + "\":" + value + "}";
        var result = await h.Call(h.Start, args); Assert.True(result.IsError);
        Assert.Equal(0, h.Fixture.Count("engine_run")); Assert.Equal(0, h.Loads);
        Assert.Equal(1, h.Fixture.Number("SELECT COUNT(*) FROM engine_tool_call WHERE outcome='refused'"));
    }

    [Fact]
    public async Task Worker_cannot_start_owner_workflow_and_stale_tokens_cannot_read_format()
    {
        await using var h = new Harness(); h.Fixture.SeedSession("worker", parentId: "owner", epoch: 3);
        var worker = await h.Call(h.Start, Start, "worker"); Assert.True(worker.IsError); Assert.Contains("top-level", Text(worker));
        Assert.Equal(0, h.Loads); Assert.Equal(0, h.Fixture.Count("engine_run"));
        var stale = await h.Call(new WorkflowFormatTool(h.Gate), "{}", epoch: 2); Assert.True(stale.IsError);
        Assert.Contains("moved", Text(stale));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("root")]
    public async Task Check_and_start_resolve_exact_added_project_root_or_unique_title(string selector)
    {
        await using var h = new Harness(); var project = selector == "title" ? "Project" : h.Root;
        var suffix = ",\"project\":" + JsonSerializer.Serialize(project);
        var check = await h.Call(h.Check, "{\"path\":\".assembly/workflows/flow.yml\"" + suffix + "}"); Assert.False(check.IsError);
        var start = await h.Call(h.Start, Start[..^1] + suffix + "}"); Assert.False(start.IsError);
    }

    [Fact]
    public async Task Ambiguous_title_and_missing_association_do_not_guess_a_project()
    {
        await using var h = new Harness();
        var store = new ProjectStore(h.Fixture.Db, h.Fixture.Time);
        h.Fixture.Db.Writer.Write("test.ambiguous-project", s => store.AddOrRestore(s, h.Fixture.Machine, h.Root + "-other", "Project", null));
        Assert.True((await h.Call(h.Check, "{\"path\":\".assembly/workflows/flow.yml\",\"project\":\"Project\"}")).IsError);
        h.Fixture.Db.Writer.Write("test.unassigned", s => s.Execute("UPDATE engine_session SET project_id=NULL WHERE id='owner'"));
        Assert.True((await h.Call(h.Start, Start)).IsError); Assert.Equal(0, h.Loads); Assert.Equal(0, h.Fixture.Count("engine_run"));
    }

    [Fact]
    public async Task Check_and_start_share_actual_invalid_definition_validation_without_execution()
    {
        await using var h = new Harness(); File.WriteAllText(h.WorkflowPath, "name: Broken\nsteps: {oops: {agent: {provider: codex}, prompt: missing.md, unsupported: true}}");
        var check = await h.Call(h.Check, "{\"path\":\".assembly/workflows/flow.yml\"}"); Assert.False(check.IsError);
        Assert.False(Json(check).GetProperty("ok").GetBoolean()); Assert.NotEmpty(Json(check).GetProperty("errors").EnumerateArray());
        var start = await h.Call(h.Start, Start); Assert.True(start.IsError);
        Assert.Equal(0, h.Fixture.Count("engine_run")); Assert.Equal(0, h.Fixture.Count("engine_outbox"));
        Assert.Equal(2, h.Loads);
    }

    [Fact]
    public async Task Task_and_inputs_are_snapshotted_and_unknown_nested_task_authority_is_refused()
    {
        await using var h = new Harness();
        var supplied = Start[..^1] + ",\"task\":{\"key\":\"TSK-1\",\"title\":\"Task\",\"body\":\"Details\",\"url\":\"https://app.notion.com/p/example\"},\"inputs\":{\"extra\":true}}";
        Assert.False((await h.Call(h.Start, supplied)).IsError);
        Assert.Equal("TSK-1", h.Fixture.Text("SELECT json_extract(snapshot_json,'$.request.task.key') FROM engine_workflow_snapshot"));
        var invalid = supplied.Replace("\"body\":\"Details\"", "\"ownerSessionId\":\"victim\",\"body\":\"Details\"");
        Assert.True((await h.Call(h.Start, invalid)).IsError); Assert.Equal(1, h.Fixture.Count("engine_run"));
    }

    private const string Start = "{\"path\":\".assembly/workflows/flow.yml\",\"request\":\"Fix the bug\",\"requestKey\":\"stable-key\"}";
    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    private static JsonElement Json(CallToolResult result) { using var json = JsonDocument.Parse(Text(result)); return json.RootElement.Clone(); }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Topics _topics = new();
        private readonly EngineRuntime _runtime;
        public EngineFixture Fixture { get; }
        public string Root { get; } = Directory.CreateTempSubdirectory("workflow-tools-").FullName;
        public string WorkflowPath => Path.Combine(Root, ".assembly/workflows/flow.yml");
        public McpGate Gate { get; }
        public StartWorkflowTool Start { get; }
        public CheckWorkflowTool Check { get; }
        public int Loads { get; private set; }
        public Harness(string provider = "codex")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WorkflowPath)!);
            File.WriteAllText(WorkflowPath, "name: Tool flow\nsteps:\n  recon: {agent: {provider: codex}, prompt: prompt.md, result: recon}");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(WorkflowPath)!, "prompt.md"), "Inspect the request");
            var limits = new SessionLimits(() => 1, () => 5); Fixture = new(topics: _topics, limits: limits);
            Fixture.SeedSession("owner", epoch: 3, provider: provider);
            var store = new ProjectStore(Fixture.Db, Fixture.Time);
            var project = Fixture.Db.Writer.Write("test.project", s => store.AddOrRestore(s, Fixture.Machine, Root, "Project", null).Project);
            Fixture.Db.Writer.Write("test.association", s => s.Execute("UPDATE engine_session SET project_id=$project WHERE id='owner'", new SqlArg("$project", project.Id)));
            _runtime = EngineRuntime.Start(Fixture.Db, _topics, Fixture.Time, limits); Gate = new(_runtime, limits, Fixture.Time);
            Assembly.Engine.Workflows.Definitions.WorkflowSnapshot Load(string root, string path, CancellationToken ct)
            { Loads++; return WorkflowLoader.Load(root, path, ct); }
            Start = new(Gate, Load); Check = new(Gate, Load);
        }
        public async Task<CallToolResult> Call(EngineTool tool, string args, string session = "owner", long epoch = 3)
        {
            using var input = new MemoryStream(); using var output = new MemoryStream();
            await using var server = McpServer.Create(new StreamServerTransport(input, output), new());
            var request = new RequestContext<CallToolRequestParams>(server, new() { Method = "tools/call", Id = new RequestId(Guid.NewGuid().ToString()) },
                new() { Name = tool.ProtocolTool.Name, Arguments = JsonSerializer.Deserialize(args, McpJson.Default.DictionaryStringJsonElement)! })
            { User = new ClaimsPrincipal(new ClaimsIdentity([new("assembly:session", session), new("assembly:epoch", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))], "test")) };
            return await new McpRecorder(_runtime, Gate, Fixture.Time).Filter((r, ct) => tool.InvokeAsync(r, ct))(request, default);
        }
        public async ValueTask DisposeAsync() { await _runtime.DisposeAsync(); Fixture.Dispose(); Directory.Delete(Root, true); }
    }
}
