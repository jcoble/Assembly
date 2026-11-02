using System.Security.Claims;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Mcp;

public sealed partial class StartSessionToolTests
{
    [Fact]
    public async Task Start_creates_child_and_queued_prefaced_prompt_together()
    {
        await using var h = new Harness();
        h.Fixture.SeedSession("owner");
        var response = await h.Call(h.Start, "owner", """{"prompt":"Build the feature","title":"Helper","taskUrl":"https://example.com/task"}""");
        Assert.False(response.IsError);
        using var json = JsonDocument.Parse(Text(response));
        var child = json.RootElement.GetProperty("sessionId").GetString()!;
        var message = json.RootElement.GetProperty("messageId").GetString()!;
        Assert.True(Guid.TryParse(child, out _));
        Assert.True(Guid.TryParse(message, out _));
        Assert.Equal("starting", h.Fixture.State("engine_session", child));
        Assert.Equal("owner", h.Fixture.Text("SELECT parent_id FROM engine_session WHERE id=$id", new SqlArg("$id", child)));
        Assert.Null(h.Fixture.Text("SELECT run_id FROM engine_session WHERE id=$id", new SqlArg("$id", child)));
        Assert.Equal("https://example.com/task", h.Fixture.Text("SELECT task_url FROM engine_session WHERE id=$id", new SqlArg("$id", child)));
        Assert.Equal("model", h.Fixture.Text("SELECT model FROM engine_session WHERE id=$id", new SqlArg("$id", child)));
        Assert.Equal("/test", h.Fixture.Text("SELECT folder FROM engine_session WHERE id=$id", new SqlArg("$id", child)));
        Assert.Equal("queued", h.Fixture.State("engine_message", message));
        Assert.Equal("Session \"Test\" (owner) started you. Do the task below. Your last reply each turn goes back to it as a message; you can also message it with the Assembly MCP server's send_message tool.\n\nBuild the feature",
            h.Fixture.Text("SELECT text FROM engine_message WHERE id=$id", new SqlArg("$id", message)));
        Assert.Equal(1, h.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        Assert.Equal(0, h.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
    }

    [Fact]
    public async Task Other_provider_uses_its_actual_adapter_default_and_explicit_model_is_kept()
    {
        await using var h = new Harness();
        h.Fixture.SeedSession("owner");
        Assert.False((await h.Call(h.Start, "owner", """{"prompt":"Task","title":"Helper","provider":"claude"}""")).IsError);
        Assert.Null(h.Fixture.Text("SELECT model FROM engine_session WHERE parent_id='owner'"));
        Assert.False((await h.Call(h.Start, "owner", """{"prompt":"Task","title":"Other","provider":"claude","model":"specific"}""")).IsError);
        Assert.Equal("specific", h.Fixture.Text("SELECT model FROM engine_session WHERE title='Other'"));
    }

    [Fact]
    public async Task Configured_custom_provider_and_unknown_caller_model_do_not_require_a_default_catalog()
    {
        await using var h = new Harness();
        h.Fixture.SeedSession("owner", provider: "work-profile");
        h.Fixture.Db.Writer.Write("test.unknown-model", scope => scope.Execute("UPDATE engine_session SET model=NULL WHERE id='owner'"));
        Assert.Null(h.Gate.Read(new("owner", 0), (caller, _) => caller.Model, default));
        var response = await h.Call(h.Start, "owner", """{"prompt":"Task","title":"Helper"}""");
        Assert.False(response.IsError);
        Assert.Equal("work-profile", h.Fixture.Text("SELECT provider FROM engine_session WHERE parent_id='owner'"));
        Assert.Null(h.Fixture.Text("SELECT model FROM engine_session WHERE parent_id='owner'"));
    }

    [Fact]
    public async Task Omitted_folder_inherits_actual_managed_policy_but_an_explicit_same_folder_is_direct()
    {
        await using var h = new Harness(); h.Fixture.SeedSession("owner");
        h.Fixture.Db.Writer.Write("test.managed", scope => scope.Execute("UPDATE engine_session SET managed_checkout=1 WHERE id='owner'"));
        Assert.False((await h.Call(h.Start, "owner", """{"prompt":"Task","title":"Inherited"}""")).IsError);
        Assert.Equal(1, h.Fixture.Number("SELECT managed_checkout FROM engine_session WHERE title='Inherited'"));
        Assert.False((await h.Call(h.Start, "owner", """{"prompt":"Task","title":"Explicit","folder":"/test"}""")).IsError);
        Assert.Equal(0, h.Fixture.Number("SELECT managed_checkout FROM engine_session WHERE title='Explicit'"));
    }

    [Fact]
    public async Task Unconfigured_provider_names_the_configured_choices()
    {
        await using var h = new Harness();
        h.Fixture.SeedSession("owner");
        var tool = new StartSessionTool(h.Gate, () => ["codex"], _ => true);
        var result = await h.Call(tool, "owner", """{"prompt":"Task","title":"Helper","provider":"claude","model":"explicit"}""");
        Assert.True(result.IsError);
        Assert.Equal("start_session: provider must be one of the configured providers: codex.", Text(result));
        Assert.Equal(1, h.Fixture.Count("engine_session"));
    }

    [Theory]
    [InlineData("{}", "prompt")]
    [InlineData("{\"prompt\":\"\",\"title\":\"Helper\"}", "prompt")]
    [InlineData("{\"prompt\":\"Task\"}", "title")]
    [InlineData("{\"prompt\":\"Task\",\"title\":\"two\\nlines\"}", "title")]
    [InlineData("{\"prompt\":\"Task\",\"title\":\"Helper\",\"provider\":\"unknown\"}", "provider")]
    [InlineData("{\"prompt\":\"Task\",\"title\":\"Helper\",\"model\":\"\"}", "model")]
    [InlineData("{\"prompt\":\"Task\",\"title\":\"Helper\",\"folder\":\"relative\"}", "folder")]
    [InlineData("{\"prompt\":\"Task\",\"title\":\"Helper\",\"folder\":\"/missing\"}", "folder")]
    [InlineData("{\"prompt\":\"Task\",\"title\":\"Helper\",\"taskUrl\":\"http://example.com\"}", "taskUrl")]
    [InlineData("{\"prompt\":true,\"title\":\"Helper\"}", "prompt")]
    [InlineData("{\"prompt\":null,\"title\":\"Helper\"}", "prompt")]
    [InlineData("{\"prompt\":\"Task\",\"title\":\"Helper\",\"actor\":\"window\"}", "actor")]
    public async Task Invalid_arguments_name_the_field_and_write_nothing(string arguments, string field)
    {
        await using var h = new Harness();
        h.Fixture.SeedSession("owner");
        var result = await h.Call(h.Start, "owner", arguments);
        Assert.True(result.IsError);
        Assert.StartsWith($"start_session: {field} ", Text(result));
        Assert.Equal(1, h.Fixture.Count("engine_session"));
        Assert.Equal(0, h.Fixture.Count("engine_message"));
        Assert.Empty(h.Fixture.Events());
        Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
    }

    [Theory]
    [InlineData("prompt", 16000, false)]
    [InlineData("prompt", 16001, true)]
    [InlineData("title", 120, false)]
    [InlineData("title", 121, true)]
    public async Task Argument_length_boundaries_are_enforced(string field, int length, bool refused)
    {
        await using var h = new Harness();
        h.Fixture.SeedSession("owner");
        using var task = JsonDocument.Parse("\"Task\"");
        using var title = JsonDocument.Parse("\"Helper\"");
        using var value = JsonDocument.Parse("\"" + new string('a', length) + "\"");
        var values = new Dictionary<string, JsonElement>
        {
            ["prompt"] = task.RootElement.Clone(),
            ["title"] = title.RootElement.Clone(),
            [field] = value.RootElement.Clone()
        };
        var result = await h.Call(h.Start, "owner", JsonSerializer.Serialize(values, McpJson.Default.DictionaryStringJsonElement));
        Assert.Equal(refused, result.IsError == true);
        Assert.Equal(refused ? 1 : 2, h.Fixture.Count("engine_session"));
    }

    [Theory]
    [InlineData(1, 1, "Sessions 2 levels down can't start sessions (the limit is 1, set by \"Session depth limit\" in Settings); ask your starter to start it.")]
    [InlineData(9, 9, "Sessions 10 levels down can't start sessions (the limit is 9, set by \"Session depth limit\" in Settings); ask your starter to start it.")]
    [InlineData(9, 1, "Sessions 10 levels down can't start sessions (the limit is 1, set by \"Session depth limit\" in Settings); ask your starter to start it.")]
    public async Task Refusal_reports_actual_depth_and_current_limit(int callerDepth, int limit, string expected)
    {
        await using var h = new Harness(new(() => limit, () => 5));
        SessionLimitTests.SeedChain(h.Fixture, callerDepth);
        var response = await h.Call(h.Start, $"node-{callerDepth}", """{"prompt":"Task","title":"Helper"}""");
        Assert.True(response.IsError);
        Assert.Equal(expected, Text(response));
    }

    [Fact]
    public async Task Start_above_three_uses_the_same_runtime_limits()
    {
        await using var h = new Harness(new(() => 9, () => 5));
        SessionLimitTests.SeedChain(h.Fixture, 8);
        var response = await h.Call(h.Start, "node-8", """{"prompt":"Task","title":"Helper"}""");
        Assert.False(response.IsError);
        Assert.Equal(10, h.Fixture.Count("engine_session"));
        Assert.Equal(1, h.Fixture.Count("engine_message"));
    }

    [Fact]
    public async Task Second_command_refusal_rolls_back_child_prompt_receipts_events_outbox_and_publications()
    {
        await using var h = new Harness(messageLimit: () => 0);
        h.Fixture.SeedSession("owner");
        string[] tables = ["engine_session", "engine_message", "engine_command_receipt", "engine_event", "engine_outbox"];
        var before = tables.Select(h.Fixture.Count).ToArray();
        var publications = 0;
        using var subscription = h.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => Interlocked.Increment(ref publications));
        var result = await h.Call(h.Start, "owner", """{"prompt":"Task","title":"Helper"}""");
        Assert.True(result.IsError);
        Assert.Equal("You've reached the message limit for that session.", Text(result));
        Assert.Equal(before, tables.Select(h.Fixture.Count).ToArray());
        Assert.Equal(0, publications);
    }

    [Fact]
    public async Task List_returns_local_nonended_relations_in_lexical_and_updated_order()
    {
        await using var h = new Harness();
        var f = h.Fixture;
        f.SeedSession("top");
        f.SeedSession("caller", parentId: "top");
        f.SeedSession("sibling", parentId: "top");
        f.SeedSession("child-old", parentId: "caller");
        f.Time.Advance(TimeSpan.FromMilliseconds(1));
        f.SeedSession("child-new", parentId: "caller");
        f.SeedSession("other");
        f.SeedSession("grandchild", parentId: "child-new");
        f.SeedSession("cousin", parentId: "sibling");
        f.SeedSession("ended", "ended");
        f.SeedSession("mirror");
        f.Db.Writer.Write("test.machine", s => s.Execute("UPDATE engine_session SET machine='elsewhere' WHERE id='mirror'"));
        var result = await h.Call(new ListSessionsTool(h.Gate), "caller", "{}");
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(Text(result));
        var rows = json.RootElement.GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(["child:child-new", "child:child-old", "other:other", "sibling:sibling", "starter:top"],
            rows.Select(r => r.GetProperty("relation").GetString() + ":" + r.GetProperty("sessionId").GetString()));
        Assert.All(rows, r => Assert.False(r.TryGetProperty("machine", out _)));
        h.Gate.Read(new("caller", 0), (_, scope) =>
        {
            Assert.True(SessionDirectory.CanReach(scope, "caller", "child-new"));
            Assert.True(SessionDirectory.CanReach(scope, "caller", "top"));
            Assert.True(SessionDirectory.CanReach(scope, "caller", "sibling"));
            Assert.True(SessionDirectory.CanReach(scope, "caller", "other"));
            Assert.False(SessionDirectory.CanReach(scope, "caller", "caller"));
            Assert.False(SessionDirectory.CanReach(scope, "caller", "mirror"));
            Assert.False(SessionDirectory.CanReach(scope, "caller", "ended"));
            Assert.False(SessionDirectory.CanReach(scope, "caller", "grandchild"));
            Assert.False(SessionDirectory.CanReach(scope, "caller", "cousin"));
            Assert.All(SessionDirectory.Reachable(scope, "caller"), row => Assert.Equal(f.Machine, row.Machine));
            return 0;
        }, default);
    }

    [Fact]
    public async Task Directory_limits_in_SQL_and_pair_lookup_does_not_inherit_the_page_limit()
    {
        await using var h = new Harness();
        h.Fixture.SeedSession("caller");
        h.Fixture.Db.Writer.Write("test.directory", s => s.Execute("""
            WITH RECURSIVE n(value) AS (VALUES(1) UNION ALL SELECT value+1 FROM n WHERE value<205)
            INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,state,
                activity_state,holder,epoch,cancel_requested)
            SELECT 'peer-'||value,0,$machine,1000,value,'codex','model','/test','Peer','idle','idle','local',0,0 FROM n
            """, new SqlArg("$machine", h.Fixture.Machine)));
        var response = await h.Call(new ListSessionsTool(h.Gate), "caller", "{}");
        using var json = JsonDocument.Parse(Text(response));
        var rows = json.RootElement.GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(200, rows.Length);
        Assert.Equal("peer-205", rows[0].GetProperty("sessionId").GetString());
        Assert.Equal("peer-6", rows[^1].GetProperty("sessionId").GetString());
        Assert.True(h.Gate.Read(new("caller", 0), (_, scope) => SessionDirectory.CanReach(scope, "caller", "peer-1"), default));
        Assert.True((await h.Call(new ListSessionsTool(h.Gate), "caller", """{"machine":"elsewhere"}""")).IsError);
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private sealed class Harness : IAsyncDisposable
    {
        public Topics Topics { get; } = new();
        public EngineFixture Fixture { get; }
        private readonly EngineRuntime _runtime;
        public McpGate Gate { get; }
        public StartSessionTool Start { get; }

        public Harness(SessionLimits? limits = null, Func<int>? messageLimit = null)
        {
            limits ??= new(() => 1, () => 5);
            Fixture = new(topics: Topics, limits: limits);
            _runtime = EngineRuntime.Start(Fixture.Db, Topics, Fixture.Time, limits, messageLimit: messageLimit);
            Gate = new(_runtime, limits, Fixture.Time);
            Start = new(Gate, () => ["codex", "claude", "work-profile"], folder => folder != "/missing");
        }

        public async Task<CallToolResult> Call(EngineTool tool, string session, string arguments)
        {
            using var input = new MemoryStream();
            using var output = new MemoryStream();
            await using var server = McpServer.Create(new StreamServerTransport(input, output), new());
            var values = JsonSerializer.Deserialize(arguments, McpJson.Default.DictionaryStringJsonElement)!;
            var request = new RequestContext<CallToolRequestParams>(server, new() { Method = "tools/call", Id = new RequestId(1) },
                new() { Name = tool.ProtocolTool.Name, Arguments = values })
            { User = new ClaimsPrincipal(new ClaimsIdentity([new("assembly:session", session), new("assembly:epoch", "0")], "test")) };
            return await tool.InvokeAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            await _runtime.DisposeAsync();
            Fixture.Dispose();
        }
    }
}
