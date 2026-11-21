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
