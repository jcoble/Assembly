using System.Security.Claims;
using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.NeedsMe;
using Assembly.Infrastructure.Engine;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Mcp;

public sealed class NeedToolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Answer_uses_trusted_caller_exact_need_and_full_answer_with_original_wait(bool form)
    {
        await using var h = new Harness();
        using var original = new CancellationTokenSource();
        var answer = form ? JsonSerializer.SerializeToElement(new { action = "accept", content = new { comment = new string('界', 40000) + "\0\nexact" } })
            : JsonSerializer.SerializeToElement("exact-provider-option");
        h.Needs.Handle = (_, _, _, _) => Task.FromResult(new CommandResult(true, false, null, 2, 1, 0));
        var request = h.Request("answer_need", new Dictionary<string, JsonElement>
        { ["needId"] = JsonSerializer.SerializeToElement("need/child"), ["answer"] = answer });
        var result = await h.Call(new AnswerNeedTool(h.Gate, h.Needs), request, original.Token);
        Assert.False(result.IsError);
        Assert.Equal("{\"needId\":\"need/child\",\"state\":\"answered\"}", Text(result));
        Assert.Equal(new McpCaller("parent", 7), h.Needs.Caller);
        Assert.Equal("need/child", h.Needs.Request!.NeedId); Assert.Equal(ParentNeedAction.Answer, h.Needs.Request.Action);
        Assert.True(JsonElement.DeepEquals(answer, h.Needs.Request.Answer!.Value));
        Assert.Equal(CancellationToken.None, h.Needs.Work); Assert.Equal(original.Token, h.Needs.Wait);
        Assert.Equal("ok", h.Fixture.Text("SELECT outcome FROM engine_tool_call"));
        Assert.False(request.Items.ContainsKey("assembly.internal.original-request-wait/v1"));
    }

    [Fact]
    public async Task Escalation_uses_same_port_without_an_answer_or_new_question()
    {
        await using var h = new Harness();
        h.Needs.Handle = (_, _, _, _) => Task.FromResult(new CommandResult(true, false, null, 2, 1, 0));
        var result = await h.Call(new AskOwnerTool(h.Gate, h.Needs), h.Request("ask_owner", """{"childNeedId":"need/child"}"""));
        Assert.False(result.IsError); Assert.Equal("{\"needId\":\"need/child\",\"state\":\"open\"}", Text(result));
        Assert.Equal(ParentNeedAction.Escalate, h.Needs.Request!.Action); Assert.Null(h.Needs.Request.Answer);
        Assert.Equal(new McpCaller("parent", 7), h.Needs.Caller); Assert.Equal(0, h.Fixture.Count("engine_needs_me"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_cancellation_detaches_the_tool_wait_and_records_failure_without_canceling_work(bool escalate)
    {
        await using var h = new Harness();
        using var original = new CancellationTokenSource();
        using var emergency = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Needs.Handle = async (_, _, work, wait) =>
        {
            Assert.Equal(CancellationToken.None, work); entered.TrySetResult();
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(wait, emergency.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, waiting.Token);
            throw new InvalidOperationException("The routed outcome unexpectedly completed.");
        };
        McpServerTool tool = escalate ? new AskOwnerTool(h.Gate, h.Needs) : new AnswerNeedTool(h.Gate, h.Needs);
        var request = h.Request(tool.ProtocolTool.Name, escalate ? """{"childNeedId":"need/child"}""" : """{"needId":"need/child","answer":"yes"}""");
        var call = h.Call(tool, request, original.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); original.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal("failed", h.Fixture.Text("SELECT outcome FROM engine_tool_call"));
            Assert.Equal("String(6) ", h.Fixture.Text("SELECT output_preview FROM engine_tool_call"));
            Assert.False(h.Needs.Work.IsCancellationRequested);
            Assert.False(request.Items.ContainsKey("assembly.internal.original-request-wait/v1"));
        }
        finally { emergency.Cancel(); original.Cancel(); try { await call; } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData("forbidden", "That question isn't yours to answer.")]
    [InlineData("bad_answer", "Use an exact optionId supplied by the question, or its original form action and content object.")]
    public async Task Service_refusal_keeps_raw_text_error_and_truthful_audit(string error, string expected)
    {
        await using var h = new Harness();
        h.Needs.Handle = (_, _, _, _) => Task.FromResult(new CommandResult(false, false, error, 1, 0, 0));
        var result = await h.Call(new AnswerNeedTool(h.Gate, h.Needs), h.Request("answer_need", """{"needId":"n","answer":"yes"}"""));
        Assert.True(result.IsError); Assert.Equal(expected, Text(result));
        Assert.Equal("refused", h.Fixture.Text("SELECT outcome FROM engine_tool_call"));
    }

    [Theory]
    [InlineData("{\"needId\":\"n\"}")]
    [InlineData("{\"needId\":\"n\",\"answer\":null}")]
    [InlineData("{\"needId\":\" \",\"answer\":\"yes\"}")]
    [InlineData("{\"needId\":\"n\",\"answer\":\"yes\",\"sessionId\":\"forged\"}")]
    public async Task Invalid_answer_arguments_never_reach_service(string arguments)
    {
        await using var h = new Harness();
        var result = await h.Call(new AnswerNeedTool(h.Gate, h.Needs), h.Request("answer_need", arguments));
        Assert.True(result.IsError); Assert.Equal(0, h.Needs.Calls);
    }

    [Fact]
    public async Task Stale_caller_is_refused_before_service_and_missing_capture_is_diagnostic()
    {
        await using var h = new Harness();
        var tool = new AnswerNeedTool(h.Gate, h.Needs);
        var stale = h.Request("answer_need", """{"needId":"n","answer":"yes"}""", epoch: 6);
        using (McpRequestWait.Capture(stale, CancellationToken.None))
        {
            var result = await tool.InvokeAsync(stale);
            Assert.True(result.IsError); Assert.Contains("moved", Text(result)); Assert.Equal(0, h.Needs.Calls);
        }
        var missing = h.Request("answer_need", """{"needId":"n","answer":"yes"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tool.InvokeAsync(missing));
        Assert.Equal(0, h.Needs.Calls);
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private sealed class ProbeNeeds : INeedAnswers
    {
        public int Calls; public McpCaller? Caller; public ParentNeedRequest? Request; public CancellationToken Work; public CancellationToken Wait;
        public Func<McpCaller, ParentNeedRequest, CancellationToken, CancellationToken, Task<CommandResult>> Handle { get; set; } =
            (_, _, _, _) => throw new InvalidOperationException("Invalid input reached the need-answer service.");
        public Task<CommandResult> ApplyAsync(McpCaller caller, ParentNeedRequest request, CancellationToken workCancellation, CancellationToken waitCancellation)
        {
            Calls++; Caller = caller; Request = request; Work = workCancellation; Wait = waitCancellation;
            return Handle(caller, request, workCancellation, waitCancellation);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Topics _topics = new();
        private readonly MemoryStream _input = new(); private readonly MemoryStream _output = new();
        private readonly McpServer _server;
        private readonly EngineRuntime _runtime;
        public EngineFixture Fixture { get; }
        public McpGate Gate { get; }
        public ProbeNeeds Needs { get; } = new();
        public Harness()
        {
            Fixture = new(topics: _topics); Fixture.SeedSession("parent", epoch: 7);
            _runtime = EngineRuntime.Start(Fixture.Db, _topics, Fixture.Time, Fixture.Limits);
            Gate = new(_runtime, Fixture.Limits, Fixture.Time);
            _server = McpServer.Create(new StreamServerTransport(_input, _output), new());
        }
        public RequestContext<CallToolRequestParams> Request(string name, string args, long epoch = 7) =>
            Request(name, JsonSerializer.Deserialize(args, McpJson.Default.DictionaryStringJsonElement)!, epoch);
        public RequestContext<CallToolRequestParams> Request(string name, Dictionary<string, JsonElement> args, long epoch = 7) =>
            new(_server, new() { Method = "tools/call", Id = new RequestId(Guid.NewGuid().ToString()) }, new() { Name = name, Arguments = args })
            { User = new ClaimsPrincipal(new ClaimsIdentity([new("assembly:session", "parent"), new("assembly:epoch", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))], "test")) };
        public Task<CallToolResult> Call(McpServerTool tool, RequestContext<CallToolRequestParams> request, CancellationToken original = default) =>
            new McpRecorder(_runtime, Gate, Fixture.Time).Filter((context, work) => tool.InvokeAsync(context, work))(request, original).AsTask();
        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync(); await _runtime.DisposeAsync(); Fixture.Dispose(); _input.Dispose(); _output.Dispose();
        }
    }
}
