using System.Security.Claims;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Prs;
using Assembly.Engine.Runs;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Mcp;

public sealed partial class ReportToolTests
{
    private const string Result = """{"state":"succeeded","summary":"done","proofUrls":["https://drive.google.com/x"]}""";
    private const string WithPr = """{"state":"succeeded","summary":"done","proofUrls":["https://drive.google.com/x"],"pr":{"url":"https://github.com/jcoble/Assembly/pull/131","head":"abcdef0123456789","state":"open"}}""";
    private const string PrOnly = """{"runId":"r","pr":{"url":"https://github.com/jcoble/Assembly/pull/131","head":"abcdef0123456789","state":"merged"}}""";
    private static readonly EngineActor Worker = new(ActorKind.Session, "worker");
    private static readonly SetStepState Success = new("succeeded", 1, ["https://drive.google.com/x"], "done", false);
    private static readonly ReportPr Claim = new("r", "jcoble/Assembly", 131, "abcdef0123456789", "https://github.com/jcoble/Assembly/pull/131", "open");

    private static void SeedWorker(EngineFixture f)
    {
        f.SeedSession("owner", epoch: 3); f.SeedRun(ownerSessionId: "owner", state: "running");
        f.SeedSession("worker", parentId: "owner", runId: "r", epoch: 7);
        f.SeedStep("step", maxAttempts: 2); f.SeedStep("next");
        Assert.True(f.Apply(new StartStep("worker"), "step", epoch: 7).Applied);
    }

    [Fact]
    public async Task Worker_report_succeeds_step_with_proof_and_pr_atomically()
    {
        await using var h = new Harness(); SeedWorker(h.Fixture);
        var response = await h.Call(new ReportTool(h.Gate), "worker", WithPr, 7);
        Assert.False(response.IsError); using var json = JsonDocument.Parse(Text(response));
        Assert.Equal("step", json.RootElement.GetProperty("stepId").GetString());
        Assert.True(Guid.TryParse(json.RootElement.GetProperty("prId").GetString(), out _));
        Assert.Equal("succeeded", h.Fixture.State("engine_step", "step"));
        Assert.Equal("[\"https://drive.google.com/x\"]", h.Fixture.Text("SELECT proof_json FROM engine_step WHERE id='step'"));
        Assert.Equal("done", h.Fixture.Text("SELECT outcome_reason FROM engine_step WHERE id='step'"));
        Assert.Equal("not_created", h.Fixture.Text("SELECT state FROM engine_pr"));
        Assert.Equal("open", h.Fixture.Text("SELECT reported_state FROM engine_pr"));
        Assert.Equal("worker", h.Fixture.Text("SELECT reported_by FROM engine_pr"));
        Assert.Equal(131, h.Fixture.Number("SELECT number FROM engine_pr"));
        Assert.Equal("jcoble/Assembly", h.Fixture.Text("SELECT repo FROM engine_pr"));
        Assert.Equal("succeeded", h.Fixture.Text("SELECT state FROM engine_step_attempt"));
    }

    [Fact]
    public async Task Same_report_twice_with_distinct_calls_changes_step_once()
    {
        await using var h = new Harness(); SeedWorker(h.Fixture);
        Assert.False((await h.Call(new ReportTool(h.Gate), "worker", Result, 7)).IsError);
        var events = h.Fixture.Count("engine_event"); var version = h.Fixture.Number("SELECT version FROM engine_step WHERE id='step'");
        Assert.False((await h.Call(new ReportTool(h.Gate), "worker", Result, 7)).IsError);
        Assert.Equal(events, h.Fixture.Count("engine_event"));
        Assert.Equal(version, h.Fixture.Number("SELECT version FROM engine_step WHERE id='step'"));
        Assert.Equal(3, h.Fixture.Count("engine_command_receipt")); // start plus two distinct report commands
    }

    [Fact]
    public void Engine_identical_report_is_noop_but_guards_still_fence_it()
    {
        using var f = new EngineFixture(); SeedWorker(f);
        Assert.True(f.Apply(Success, "step", 1, 7, Worker).Applied);
        var events = f.Count("engine_event");
        var duplicate = f.Apply(Success, "step", 2, 7, Worker);
        Assert.True(duplicate.Applied); Assert.Equal(2, duplicate.Version); Assert.Equal(events, f.Count("engine_event"));
        Assert.Equal("version_conflict", f.Apply(Success, "step", 1, 7, Worker).Error);
        Assert.Equal("wrong_attempt", f.Apply(Success with { Attempt = 2 }, "step", 2, 7, Worker).Error);
        Assert.Equal("stale_epoch", f.Apply(Success, "step", 2, 6, Worker).Error);
        Assert.Equal("forbidden", f.Apply(Success, "step", 2, 7, new(ActorKind.Session, "owner")).Error);
        Assert.Equal("invalid_transition", f.Apply(Success with { Reason = "changed" }, "step", 2, 7, Worker).Error);
        Assert.Equal("invalid_transition", f.Apply(Success with { ProofUrls = ["https://other.test/proof"] }, "step", 2, 7, Worker).Error);
        Assert.Equal(events, f.Count("engine_event"));
    }

    [Theory]
    [InlineData("running", true)]
    [InlineData("waiting", true)]
    [InlineData("succeeded", false)]
    [InlineData("failed", false)]
    public void Engine_worker_pr_authority_is_only_its_assigned_live_step(string state, bool allowed)
    {
        using var f = new EngineFixture(); SeedWorker(f);
        f.Db.Writer.Write("test.step", s => s.Execute("UPDATE engine_step SET state=$state WHERE id='step'", new SqlArg("$state", state)));
        var result = f.Apply(Claim, "p", epoch: 3, actor: Worker);
        Assert.Equal(allowed, result.Applied);
        if (!allowed) Assert.Equal("forbidden", result.Error);
        else Assert.Equal("not_created", f.State("engine_pr", "p"));
    }

    [Fact]
    public void Foreign_worker_and_terminal_run_cannot_report_pr()
    {
        using var f = new EngineFixture(); SeedWorker(f); f.SeedSession("foreign", parentId: "owner", runId: "r");
        Assert.Equal("forbidden", f.Apply(Claim, "p", epoch: 3, actor: new(ActorKind.Session, "foreign")).Error);
        Assert.Equal("stale_epoch", f.Apply(Claim, "p", epoch: 7, actor: Worker).Error);
        f.Db.Writer.Write("test.terminal", s => s.Execute("UPDATE engine_run SET state='succeeded' WHERE id='r'"));
        Assert.False(f.Apply(Claim, "p", epoch: 3, actor: new(ActorKind.Session, "owner")).Applied);
        Assert.Equal(0, f.Count("engine_pr"));
    }

    [Fact]
    public async Task Owner_reports_pr_without_step_and_cannot_claim_merge()
    {
        await using var h = new Harness(); SeedWorker(h.Fixture);
        var response = await h.Call(new ReportTool(h.Gate), "owner", PrOnly, 3);
        Assert.False(response.IsError); using var json = JsonDocument.Parse(Text(response));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("stepId").ValueKind);
        Assert.Equal("not_created", h.Fixture.Text("SELECT state FROM engine_pr"));
        Assert.Equal("merged", h.Fixture.Text("SELECT reported_state FROM engine_pr"));
        var id = json.RootElement.GetProperty("prId").GetString()!;
        Assert.True(h.Fixture.Apply(new ObservePr("merged", Claim.Head, 2000), id, 1, 3, new(ActorKind.System, "observer")).Applied);
        Assert.Equal("merged", h.Fixture.State("engine_pr", id));
        var second = await h.Call(new ReportTool(h.Gate), "owner", PrOnly.Replace("/131", "/132"), 3);
        Assert.True(second.IsError); Assert.Equal("That run already has a different PR; report on that one.", Text(second));
        Assert.Equal(1, h.Fixture.Count("engine_pr"));
    }

    [Theory]
    [InlineData("owner", "{\"pr\":{\"url\":\"https://github.com/jcoble/Assembly/pull/131\",\"head\":\"abcdef0\",\"state\":\"open\"}}", "report: runId is required for a run's owner.")]
    [InlineData("owner", Result, "You aren't working a workflow step, so there is nothing to report a result for.")]
    [InlineData("outside", Result, "You aren't working a workflow step, so there is nothing to report a result for.")]
    public async Task Caller_refusals_are_exact(string caller, string args, string expected)
    {
        await using var h = new Harness(); SeedWorker(h.Fixture); h.Fixture.SeedSession("outside");
        var response = await h.Call(new ReportTool(h.Gate), caller, args, caller == "owner" ? 3 : 0);
        Assert.True(response.IsError); Assert.Equal(expected, Text(response)); Assert.Equal(0, h.Fixture.Count("engine_pr"));
    }

    [Theory]
    [InlineData("http://github.com/org/repo/pull/42")]
    [InlineData("https://github.com.evil.test/org/repo/pull/42")]
    [InlineData("https://github.com@evil.test/org/repo/pull/42")]
    [InlineData("https://evil@github.com/org/repo/pull/42")]
    [InlineData("https://github.com:444/org/repo/pull/42")]
    [InlineData("https://github.com/org/repo/issues/42")]
    [InlineData("https://github.com/org/repo/pull/0")]
    [InlineData("https://github.com/org/repo/pull/2147483648")]
    [InlineData("https://github.com/org/repo/pull/42/extra")]
    [InlineData("https://github.com/org/repo/pull/42?x=1")]
    [InlineData("https://github.com/org/repo/pull/42#fragment")]
    [InlineData("https://github.com/org%2frepo/repo/pull/42")]
    public async Task Malicious_or_noncanonical_pr_urls_are_refused(string url)
    {
        await using var h = new Harness(); SeedWorker(h.Fixture);
        var args = PrOnly.Replace(Claim.Url, url);
        var response = await h.Call(new ReportTool(h.Gate), "owner", args, 3);
        Assert.True(response.IsError); Assert.Equal("report: pr.url must be a GitHub pull request URL.", Text(response));
        Assert.Equal(0, h.Fixture.Count("engine_pr"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"state\":\"running\",\"summary\":\"done\"}")]
    [InlineData("{\"state\":\"succeeded\",\"summary\":\"done\",\"proofUrls\":[\"http://proof.test\"]}")]
    [InlineData("{\"state\":\"failed\"}")]
    [InlineData("{\"state\":\"failed\",\"summary\":\"done\",\"stepId\":\"foreign\"}")]
    [InlineData("{\"state\":\"failed\",\"summary\":\"done\",\"pr\":null}")]
    [InlineData("{\"state\":\"failed\",\"summary\":\"done\",\"pr\":{\"url\":\"https://github.com/jcoble/Assembly/pull/131\",\"head\":\"nothex!\",\"state\":\"open\"}}")]
    [InlineData("{\"state\":\"failed\",\"summary\":\"done\",\"pr\":{\"url\":\"https://github.com/jcoble/Assembly/pull/131\",\"head\":\"abcdef0\",\"state\":\"open\",\"runId\":\"foreign\"}}")]
    public async Task Strict_report_arguments_write_nothing(string args)
    {
        await using var h = new Harness(); SeedWorker(h.Fixture); var before = h.Fixture.Count("engine_event");
        Assert.True((await h.Call(new ReportTool(h.Gate), "worker", args, 7)).IsError);
        Assert.Equal("running", h.Fixture.State("engine_step", "step")); Assert.Equal(0, h.Fixture.Count("engine_pr")); Assert.Equal(before, h.Fixture.Count("engine_event"));
    }

    [Fact]
    public async Task Report_failure_rolls_back_both_commands_events_receipts_and_publication()
    {
        await using var h = new Harness(); SeedWorker(h.Fixture);
        var events = h.Fixture.Count("engine_event"); var receipts = h.Fixture.Count("engine_command_receipt"); var publications = 0;
        using var subscription = h.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => Interlocked.Increment(ref publications));
        h.Fixture.Db.Writer.Write("test.fail", s => s.Execute("CREATE TRIGGER fail_step AFTER UPDATE ON engine_step BEGIN SELECT RAISE(ABORT,'injected'); END"));
        await Assert.ThrowsAsync<SqliteException>(() => h.Call(new ReportTool(h.Gate), "worker", WithPr, 7));
        Assert.Equal("running", h.Fixture.State("engine_step", "step")); Assert.Equal(0, h.Fixture.Count("engine_pr"));
        Assert.Null(h.Fixture.Text("SELECT pr_id FROM engine_run"));
        Assert.Equal(events, h.Fixture.Count("engine_event")); Assert.Equal(receipts, h.Fixture.Count("engine_command_receipt")); Assert.Equal(0, publications);
    }

    [Fact]
    public async Task Link_task_sets_only_own_session_without_workflow_and_round_trips()
    {
        await using var h = new Harness(); h.Fixture.SeedSession("solo", epoch: 5); h.Fixture.SeedSession("other");
        var response = await h.Call(new LinkTaskTool(h.Gate), "solo", """{"taskUrl":"https://app.notion.com/p/x"}""", 5);
        Assert.False(response.IsError); Assert.Equal("https://app.notion.com/p/x", h.Fixture.Text("SELECT task_url FROM engine_session WHERE id='solo'"));
        Assert.Null(h.Fixture.Text("SELECT task_url FROM engine_session WHERE id='other'"));
        Assert.Equal("session.changed", Assert.Single(h.Fixture.Events()).Kind); Assert.Equal(1, h.Fixture.Number("SELECT version FROM engine_session WHERE id='solo'"));
        Assert.Equal(0, h.Fixture.Count("engine_run"));
        var command = DecodeLink("https://example.com/task");
        Assert.Equal(command, JsonSerializer.Deserialize(JsonSerializer.Serialize(command, EngineJson.Default.EngineCommand), EngineJson.Default.EngineCommand));
        Assert.Equal("forbidden", h.Fixture.Apply(command, "other").Error);
        Assert.Equal("forbidden", h.Fixture.Apply(command, "other", actor: new(ActorKind.Session, "solo")).Error);
        Assert.Equal("stale_epoch", h.Fixture.Apply(command, "solo", 1, epoch: 0, actor: new(ActorKind.Session, "solo")).Error);
    }

    [Theory]
    [InlineData("{\"taskUrl\":\"http://example.com/task\"}")]
    [InlineData("{\"taskUrl\":\"https://\"}")]
    [InlineData("{\"taskUrl\":\"https://example.com/task\",\"sessionId\":\"other\"}")]
    public async Task Invalid_link_task_is_refused_without_changes(string args)
    {
        await using var h = new Harness(); h.Fixture.SeedSession("solo");
        Assert.True((await h.Call(new LinkTaskTool(h.Gate), "solo", args)).IsError);
        Assert.Empty(h.Fixture.Events()); Assert.Null(h.Fixture.Text("SELECT task_url FROM engine_session"));
    }

    [Fact]
    public async Task Combined_terminal_repeat_preserves_observation_and_changes_nothing()
    {
        await using var h = new Harness(); SeedWorker(h.Fixture);
        // Completing the only required assigned step terminalizes this run.
        h.Fixture.Db.Writer.Write("test.single", s => s.Execute("DELETE FROM engine_step WHERE id='next'"));
        Assert.False((await h.Call(new ReportTool(h.Gate), "worker", WithPr, 7)).IsError);
        Assert.Equal("succeeded", h.Fixture.State("engine_run", "r"));
        var id = h.Fixture.Text("SELECT id FROM engine_pr")!;
        Assert.True(h.Fixture.Apply(new ObservePr("merged", Claim.Head, 2000), id, 1, 3, new(ActorKind.System, "observer")).Applied);
        var events = h.Fixture.Count("engine_event"); var receipts = h.Fixture.Count("engine_command_receipt");
        var response = await h.Call(new ReportTool(h.Gate), "worker", WithPr, 7);
        Assert.False(response.IsError); Assert.Equal(events, h.Fixture.Count("engine_event"));
        Assert.Equal(2, h.Fixture.Number("SELECT version FROM engine_pr")); Assert.Equal("merged", h.Fixture.State("engine_pr", id));
        Assert.Equal(Claim.Head, h.Fixture.Text("SELECT observed_head FROM engine_pr")); Assert.Equal(2000, h.Fixture.Number("SELECT observed_ms FROM engine_pr"));
        Assert.Equal(receipts + 2, h.Fixture.Count("engine_command_receipt"));
        foreach (var changed in new[] { WithPr.Replace("done", "changed"), WithPr.Replace("google.com/x", "google.com/y"),
            WithPr.Replace("abcdef0123456789", "1234567"), WithPr.Replace("\"open\"", "\"closed\""), WithPr.Replace("/131", "/132"),
            WithPr.Replace("github.com", "GITHUB.com"), WithPr.Replace("succeeded", "failed") })
            Assert.True((await h.Call(new ReportTool(h.Gate), "worker", changed, 7)).IsError);
        Assert.Equal(events, h.Fixture.Count("engine_event")); Assert.Equal(receipts + 2, h.Fixture.Count("engine_command_receipt"));
        Assert.Equal("merged", h.Fixture.State("engine_pr", id));
    }

    [Fact]
    public async Task Ambiguous_completed_assignment_cannot_choose_a_step()
    {
        await using var h = new Harness(); SeedWorker(h.Fixture);
        Assert.False((await h.Call(new ReportTool(h.Gate), "worker", Result, 7)).IsError);
        h.Fixture.SeedStep("also", state: "succeeded", sessionId: "worker", attempt: 1);
        var before = h.Fixture.Count("engine_event");
        Assert.True((await h.Call(new ReportTool(h.Gate), "worker", Result, 7)).IsError);
        Assert.Equal(before, h.Fixture.Count("engine_event"));
    }

    [Fact]
    public async Task Worker_run_argument_is_ignored_and_owner_cannot_report_foreign_or_terminal_run()
    {
        await using var h = new Harness(); SeedWorker(h.Fixture);
        h.Fixture.SeedSession("foreign"); h.Fixture.SeedRun("other", "foreign");
        Assert.False((await h.Call(new ReportTool(h.Gate), "worker", WithPr.Replace("{\"state\"", "{\"runId\":\"other\",\"state\""), 7)).IsError);
        Assert.Equal("r", h.Fixture.Text("SELECT run_id FROM engine_pr"));
        Assert.True((await h.Call(new ReportTool(h.Gate), "foreign", PrOnly)).IsError);
        h.Fixture.Db.Writer.Write("test.terminal", s => s.Execute("UPDATE engine_run SET state='succeeded' WHERE id='r'"));
        Assert.True((await h.Call(new ReportTool(h.Gate), "owner", PrOnly, 3)).IsError);
    }

    [Fact]
    public async Task Link_task_preserves_run_metadata_and_rolls_back_on_journal_failure()
    {
        await using var h = new Harness(); SeedWorker(h.Fixture);
        h.Fixture.Db.Writer.Write("test.link", s => s.Execute("UPDATE engine_run SET task_url='https://old.test/task' WHERE id='r'"));
        var runVersion = h.Fixture.Number("SELECT version FROM engine_run"); var events = h.Fixture.Count("engine_event");
        Assert.False((await h.Call(new LinkTaskTool(h.Gate), "owner", "{\"taskUrl\":\"https://new.test/task\"}", 3)).IsError);
        Assert.Equal("https://old.test/task", h.Fixture.Text("SELECT task_url FROM engine_run"));
        Assert.Equal(runVersion, h.Fixture.Number("SELECT version FROM engine_run")); Assert.Equal(events + 1, h.Fixture.Count("engine_event"));
        var receipts = h.Fixture.Count("engine_command_receipt");
        h.Fixture.Db.Writer.Write("test.fail", s => s.Execute("CREATE TRIGGER fail_link AFTER INSERT ON engine_event BEGIN SELECT RAISE(ABORT,'injected'); END"));
        await Assert.ThrowsAsync<SqliteException>(() => h.Call(new LinkTaskTool(h.Gate), "owner", "{\"taskUrl\":\"https://failed.test/task\"}", 3));
        Assert.Equal("https://new.test/task", h.Fixture.Text("SELECT task_url FROM engine_session WHERE id='owner'"));
        Assert.Equal(events + 1, h.Fixture.Count("engine_event")); Assert.Equal(receipts, h.Fixture.Count("engine_command_receipt"));
    }

    [Fact]
    public void Link_task_engine_checks_https_versions_and_terminal_actor()
    {
        using var f = new EngineFixture(); f.SeedSession("solo"); var actor = new EngineActor(ActorKind.Session, "solo");
        Assert.Equal("bad_args", f.Apply(new LinkTask("http://example.com/task"), "solo", actor: actor).Error);
        Assert.Equal("version_conflict", f.Apply(new LinkTask("https://example.com/task"), "solo", 1, actor: actor).Error);
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "solo", actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("invalid_transition", f.Apply(new LinkTask("https://example.com/task"), "solo", 1, actor: actor).Error);
        Assert.Null(f.Text("SELECT task_url FROM engine_session"));
    }

    [Fact]
    public async Task Completed_pr_repeat_rejects_foreign_assignment_and_ended_actor()
    {
        await using var h = new Harness(); SeedWorker(h.Fixture);
        Assert.False((await h.Call(new ReportTool(h.Gate), "worker", WithPr, 7)).IsError);
        var id = h.Fixture.Text("SELECT id FROM engine_pr")!;
        h.Fixture.SeedSession("foreign", parentId: "owner", runId: "r", epoch: 7);
        Assert.Equal("forbidden", h.Fixture.Apply(Claim, id, 1, 3, new(ActorKind.Session, "foreign")).Error);
        h.Fixture.Db.Writer.Write("test.assignment", s => s.Execute("UPDATE engine_session SET run_id=NULL WHERE id='worker'"));
        Assert.Equal("forbidden", h.Fixture.Apply(Claim, id, 1, 3, Worker).Error);
        h.Fixture.Db.Writer.Write("test.assignment", s => s.Execute("UPDATE engine_session SET run_id='r',state='ended' WHERE id='worker'"));
        Assert.Equal("forbidden", h.Fixture.Apply(Claim, id, 1, 3, Worker).Error);
        Assert.Equal(1, h.Fixture.Number("SELECT version FROM engine_pr"));
    }

    [Fact]
    public void Nullable_claim_metadata_does_not_block_trusted_observation()
    {
        using var f = new EngineFixture(); SeedWorker(f);
        Assert.True(f.Apply(Claim, "p", epoch: 3, actor: Worker).Applied);
        f.Db.Writer.Write("test.nullable-claim", s => s.Execute("UPDATE engine_pr SET reported_by=NULL,reported_state=NULL,url=NULL WHERE id='p'"));
        Assert.True(f.Apply(new ObservePr("open", Claim.Head, 2000), "p", 1, 3, new(ActorKind.System, "observer")).Applied);
        Assert.Equal("open", f.State("engine_pr", "p"));
    }

    private static EngineCommand DecodeLink(string url) => JsonSerializer.Deserialize("{\"kind\":\"link_task\",\"taskUrl\":\"" + url + "\"}", EngineJson.Default.EngineCommand)!;
    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private sealed class Harness : IAsyncDisposable
    {
        public Topics Topics { get; } = new();
        public EngineFixture Fixture { get; }
        private readonly EngineRuntime _runtime;
        public McpGate Gate { get; }
        public Harness()
        {
            var limits = new SessionLimits(() => 1, () => 5);
            Fixture = new(topics: Topics, limits: limits);
            _runtime = EngineRuntime.Start(Fixture.Db, Topics, Fixture.Time, limits);
            Gate = new(_runtime, limits, Fixture.Time);
        }
        public async Task<CallToolResult> Call(EngineTool tool, string session, string arguments, long epoch = 0, bool record = false)
        {
            using var input = new MemoryStream(); using var output = new MemoryStream();
            await using var server = McpServer.Create(new StreamServerTransport(input, output), new());
            var values = JsonSerializer.Deserialize(arguments, McpJson.Default.DictionaryStringJsonElement)!;
            var request = new RequestContext<CallToolRequestParams>(server, new() { Method = "tools/call", Id = new RequestId(Guid.NewGuid().ToString()) },
                new() { Name = tool.ProtocolTool.Name, Arguments = values })
            { User = new ClaimsPrincipal(new ClaimsIdentity([new("assembly:session", session), new("assembly:epoch", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))], "test")) };
            return record
                ? await new McpRecorder(_runtime, Gate, Fixture.Time).Filter((r, ct) => tool.InvokeAsync(r, ct))(request, default)
                : await tool.InvokeAsync(request);
        }
        public async ValueTask DisposeAsync() { await _runtime.DisposeAsync(); Fixture.Dispose(); }
    }
}
