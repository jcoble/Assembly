using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Mcp;
using Assembly.Engine.Messages;
using Assembly.Engine.Recording;
using Assembly.Infrastructure.Engine;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Mcp;

public sealed class McpRecorderTests
{
    private static readonly EngineActor Recorder = new(ActorKind.System, "mcp");
    private static CallToolResult Answer(string text = "done", bool error = false) =>
        new() { IsError = error, Content = [new TextContentBlock { Text = text }] };

    private sealed class Harness : IAsyncDisposable
    {
        public Topics Topics { get; } = new();
        public EngineFixture Fixture { get; }
        public EngineRuntime Runtime { get; }
        public McpGate Gate { get; }
        private readonly MemoryStream _input = new();
        private readonly MemoryStream _output = new();
        private readonly McpServer _server;

        public Harness(string state = "idle", long epoch = 0, bool seed = true, bool worker = false)
        {
            Fixture = new(topics: Topics);
            if (worker)
            {
                Fixture.SeedSession("owner");
                Fixture.SeedRun(ownerSessionId: "owner");
                Fixture.SeedSession("s", "running", "owner", "r", epoch: epoch);
                Fixture.SeedStep("st", state: "running", sessionId: "s", attempt: 2);
            }
            else if (seed) Fixture.SeedSession(state: state, epoch: epoch);
            Runtime = EngineRuntime.Start(Fixture.Db, Topics, Fixture.Time, new(() => 1, () => 5));
            Gate = new(Runtime, new(() => 1, () => 5), Fixture.Time);
            _server = McpServer.Create(new StreamServerTransport(_input, _output), new());
        }

        public async Task<CallToolResult> Call(McpRequestHandler<CallToolRequestParams, CallToolResult> next,
            Dictionary<string, JsonElement>? args = null, CancellationToken token = default, IEngineRuntime? runtime = null, string name = "probe")
        {
            return await CallRequest(Request(name, args), next, token, runtime);
        }

        public RequestContext<CallToolRequestParams> Request(string name = "probe", Dictionary<string, JsonElement>? args = null) =>
            new(_server, new() { Method = "tools/call", Id = new RequestId(Guid.NewGuid().ToString()) }, new() { Name = name, Arguments = args })
            { User = new ClaimsPrincipal(new ClaimsIdentity([new("assembly:session", "s"), new("assembly:epoch", "0")], "test")) };

        public ValueTask<CallToolResult> CallRequest(RequestContext<CallToolRequestParams> request,
            McpRequestHandler<CallToolRequestParams, CallToolResult> next, CancellationToken token = default, IEngineRuntime? runtime = null) =>
            new McpRecorder(runtime ?? Runtime, Gate, Fixture.Time).Filter(next)(request, token);

        public string? Text(string column) => Fixture.Text($"SELECT {column} FROM engine_tool_call");
        public long Number(string column) => Fixture.Number($"SELECT {column} FROM engine_tool_call");
        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            await Runtime.DisposeAsync();
            Fixture.Dispose();
            _input.Dispose();
            _output.Dispose();
        }
    }

    [Fact]
    public async Task Call_is_recorded_start_and_end_with_attribution()
    {
        await using var h = new Harness(worker: true);
        await h.Call((_, _) =>
        {
            Assert.Equal(1, h.Fixture.Count("engine_tool_call"));
            Assert.Null(h.Text("outcome"));
            h.Fixture.Time.Advance(TimeSpan.FromMilliseconds(25));
            return ValueTask.FromResult(Answer());
        });
        Assert.Equal("mcp|probe|s|r|st|2|0|ok", h.Text("boundary||'|'||tool||'|'||session_id||'|'||run_id||'|'||step_id||'|'||attempt||'|'||epoch||'|'||outcome"));
        Assert.Equal(25, h.Number("ended_ms-started_ms"));
        Assert.Equal(2, h.Fixture.Events().Count(e => e.Kind is "tool.started" or "tool.ended"));
        Assert.Equal(0, h.Fixture.Count("engine_usage"));
    }

    [Fact]
    public async Task Mcp_preview_holds_no_secret_and_exact_bytes()
    {
        await using var h = new Harness();
        var args = new Dictionary<string, JsonElement> { ["sk-secret-key"] = JsonSerializer.SerializeToElement("sk-live-secret" + new string('界', 10000)) };
        var rawArgs = JsonSerializer.Serialize(args, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var result = Answer("output-secret界");
        await h.Call((_, _) => ValueTask.FromResult(result), args);
        Assert.Equal(Encoding.UTF8.GetByteCount(rawArgs), h.Number("input_bytes"));
        var output = JsonSerializer.Serialize(result, ModelContextProtocol.McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult)));
        Assert.Equal(Encoding.UTF8.GetByteCount(output), h.Number("output_bytes"));
        var previews = h.Text("input_preview||output_preview")!;
        Assert.DoesNotContain("secret", previews);
        Assert.True(Encoding.UTF8.GetByteCount(previews) <= 8192);
        Assert.All(h.Fixture.Events(), e => Assert.DoesNotContain("secret", JsonSerializer.Serialize(e, EngineJson.Default.EngineEvent)));
        Assert.DoesNotContain("secret", h.Fixture.Text("SELECT group_concat(result_json) FROM engine_command_receipt")!);
    }

    [Fact]
    public async Task Absent_arguments_record_literal_null_bytes()
    {
        await using var h = new Harness();
        await h.Call((_, _) => ValueTask.FromResult(Answer()));
        Assert.Equal(4, h.Number("input_bytes"));
        Assert.Equal("Null(4) ", h.Text("input_preview"));
    }

    [Fact]
    public async Task Refusal_is_recorded_as_refused()
    {
        await using var h = new Harness();
        Assert.True((await h.Call((_, _) => ValueTask.FromResult(Answer("refused", true)))).IsError);
        Assert.Equal("refused", h.Text("outcome"));
    }

    [Theory]
    [InlineData("idle", 1)]
    [InlineData("ended", 0)]
    [InlineData("lost", 0)]
    [InlineData("handing_off", 0)]
    public async Task Stale_epoch_token_is_refused_and_recorded(string state, long epoch)
    {
        await using var h = new Harness(state, epoch);
        var result = await h.Call((_, _) => throw new InvalidOperationException("must not execute"));
        Assert.True(result.IsError);
        Assert.Equal("refused", h.Text("outcome"));
        Assert.Equal("s", h.Text("session_id"));
        Assert.Equal(0, h.Number("epoch"));
        Assert.Null(h.Text("run_id"));
        Assert.NotNull(h.Text("ended_ms"));
        if (epoch == 1) Assert.Contains("moved", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task Foreign_stub_cannot_become_a_local_caller_or_recorded_tool_actor()
    {
        await using var h = new Harness(seed: false);
        Assert.True(h.Fixture.Apply(new Assembly.Engine.Sessions.RegisterForeignSession("source", "p", "T"), "s",
            actor: new(ActorKind.System, "machine:source")).Applied);
        Assert.Null(((IEngineRuntime)h.Runtime).Read(scope => h.Gate.Resolve(scope, new("s", 0)), default));
        var receipts = h.Fixture.Count("engine_command_receipt");
        Assert.True((await h.Call((_, _) => throw new InvalidOperationException("must not execute"))).IsError);
        Assert.Equal(0, h.Fixture.Count("engine_tool_call")); Assert.Empty(h.Fixture.Events());
        Assert.Equal(receipts, h.Fixture.Count("engine_command_receipt"));
    }

    [Fact]
    public async Task Missing_session_is_the_only_unrecorded_case()
    {
        await using var h = new Harness(seed: false);
        Assert.True((await h.Call((_, _) => throw new InvalidOperationException())).IsError);
        Assert.Equal(0, h.Fixture.Count("engine_tool_call"));
    }

    [Fact]
    public async Task Client_gone_after_commit_keeps_row_and_records_end()
    {
        await using var h = new Harness();
        h.Fixture.SeedSession("to");
        using var gone = new CancellationTokenSource();
        await h.Call((_, token) =>
        {
            Assert.False(token.CanBeCanceled);
            h.Gate.Write(new("s", 0), w => w.Apply("msg", 0, 0, new SendMessage("s", "to", "hello", "prompt")));
            gone.Cancel();
            return ValueTask.FromResult(Answer());
        }, token: gone.Token);
        Assert.True(gone.IsCancellationRequested);
        Assert.Equal(1, h.Fixture.Count("engine_message"));
        Assert.Equal("ok", h.Text("outcome"));
        Assert.NotNull(h.Text("ended_ms"));
    }

    [Fact]
    public async Task Exception_is_recorded_failed_and_rethrown_without_secret()
    {
        await using var h = new Harness();
        var error = new InvalidOperationException("sk-secret-exception");
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => h.Call((_, _) => throw error)));
        Assert.Equal("failed", h.Text("outcome"));
        Assert.NotNull(h.Text("ended_ms"));
        Assert.DoesNotContain("secret", h.Text("output_preview")!);
    }

    [Fact]
    public async Task Mcp_calls_are_counted_apart_while_both_attributed_audits_remain()
    {
        await using var h = new Harness(worker: true);
        var f = h.Fixture;
        var attribution = new Attribution("s", "r", "st", 2, 0);
        Assert.True(f.Apply(new ToolStarted(attribution, "acp", "provider-call", "probe",
            JsonSerializer.SerializeToElement(new { acpKey = "input" }), 18), "acp", actor: Recorder).Applied);
        f.Time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(f.Apply(new ToolEnded(attribution, "ok", JsonSerializer.SerializeToElement(new { acpResult = "output" }), 22), "acp", actor: Recorder).Applied);
        var acpInput = f.Text("SELECT input_preview FROM engine_tool_call WHERE id='acp'");
        var acpOutput = f.Text("SELECT output_preview FROM engine_tool_call WHERE id='acp'");
        string? mcpInput = null;
        var args = new Dictionary<string, JsonElement> { ["mcpKey"] = JsonSerializer.SerializeToElement("input") };
        await h.Call((_, _) =>
        {
            mcpInput = f.Text("SELECT input_preview FROM engine_tool_call WHERE boundary='mcp'");
            var running = new UsageQueries(f.Db).Sum("session", "s", default);
            Assert.Equal(1, running.ToolCalls);
            Assert.Equal(100, running.ToolDurationMs);
            Assert.Equal(1, McpCalls(running));
            f.Time.Advance(TimeSpan.FromMilliseconds(25));
            return ValueTask.FromResult(Answer("mcp-output"));
        }, args);
        var audits = f.Db.Query("""
            SELECT boundary,session_id,run_id,step_id,attempt,epoch,input_preview,output_preview,input_bytes,output_bytes
            FROM engine_tool_call ORDER BY boundary
            """, row => (Boundary: row.Text(0), Session: row.Text(1), Run: row.Text(2), Step: row.Text(3),
                Attempt: row.Long(4), Epoch: row.Long(5), Input: row.Text(6), Output: row.Text(7), InputBytes: row.Long(8), OutputBytes: row.Long(9)), default);
        Assert.Equal(new[] { "acp", "mcp" }, audits.Select(a => a.Boundary));
        Assert.All(audits, a =>
        {
            Assert.Equal("s", a.Session); Assert.Equal("r", a.Run); Assert.Equal("st", a.Step);
            Assert.Equal(2, a.Attempt); Assert.Equal(0, a.Epoch);
            Assert.NotEmpty(a.Input); Assert.NotEmpty(a.Output); Assert.True(a.InputBytes > 0); Assert.True(a.OutputBytes > 0);
        });
        Assert.Equal(acpInput, audits[0].Input); Assert.Equal(acpOutput, audits[0].Output); Assert.Equal(mcpInput, audits[1].Input);
        var rows = f.Count("engine_tool_call"); var events = f.Count("engine_event");
        foreach (var (kind, id) in new[] { ("session", "s"), ("run", "r"), ("step", "st") })
        {
            var sum = new UsageQueries(f.Db).Sum(kind, id, default);
            Assert.Equal(1, sum.ToolCalls); Assert.Equal(1, McpCalls(sum)); Assert.Equal(100, sum.ToolDurationMs);
            Assert.Null(sum.InputTokens); Assert.Null(sum.CostNanodollars); Assert.Null(sum.TurnDurationMs);
        }
        Assert.Equal(rows, f.Count("engine_tool_call")); Assert.Equal(events, f.Count("engine_event"));
    }

    [Fact]
    public async Task Original_wait_token_crosses_the_shared_sdk_invocation_while_execution_stays_none()
    {
        await using var h = new Harness();
        using var original = new CancellationTokenSource();
        var request = h.Request();
        var previous = new object();
        const string key = "assembly.internal.original-request-wait/v1";
        request.Items[key] = previous;
        var result = await h.CallRequest(request, (context, execution) => McpInvocation.InvokeAsync("probe", context,
            (caller, _, work) =>
            {
                Assert.Equal(new McpCaller("s", 0), caller);
                Assert.Equal(CancellationToken.None, execution);
                Assert.Equal(CancellationToken.None, work);
                var wait = McpInvocation.RequireOriginalWaitToken(context);
                Assert.Equal(original.Token, wait);
                Assert.True(wait.CanBeCanceled);
                original.Cancel();
                Assert.True(wait.IsCancellationRequested);
                Assert.False(work.IsCancellationRequested);
                return ValueTask.FromResult(McpInvocation.Text("{\"exact\":\"界\"}", false));
            }, execution), original.Token);
        Assert.Equal("{\"exact\":\"界\"}", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Same(previous, request.Items[key]);
        Assert.Equal("ok", h.Text("outcome"));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("refused")]
    [InlineData("cancel")]
    [InlineData("exception")]
    [InlineData("start-failure")]
    [InlineData("end-failure")]
    [InlineData("identity")]
    [InlineData("missing-session")]
    public async Task Every_recorder_exit_restores_the_exact_prior_items_value(string mode)
    {
        await using var h = new Harness(state: mode == "refused" ? "ended" : "idle", seed: mode != "missing-session");
        using var original = new CancellationTokenSource();
        var request = h.Request();
        if (mode == "identity") request.User = null;
        const string key = "assembly.internal.original-request-wait/v1";
        var prior = new object();
        request.Items[key] = prior;
        var nextCalled = false;
        IEngineRuntime? runtime = mode is "start-failure" or "end-failure"
            ? new FailingRuntime(h.Runtime, mode == "start-failure" ? 1 : 2) : null;
        ValueTask<CallToolResult> Next(RequestContext<CallToolRequestParams> context, CancellationToken execution)
        {
            nextCalled = true;
            Assert.Equal(original.Token, McpInvocation.RequireOriginalWaitToken(context));
            Assert.Equal(CancellationToken.None, execution);
            if (mode == "cancel")
            {
                original.Cancel();
                McpInvocation.RequireOriginalWaitToken(context).ThrowIfCancellationRequested();
            }
            if (mode == "exception") throw new InvalidOperationException("safe probe");
            return ValueTask.FromResult(Answer());
        }
        if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.CallRequest(request, Next, original.Token, runtime).AsTask());
        else if (mode == "exception") await Assert.ThrowsAsync<InvalidOperationException>(() => h.CallRequest(request, Next, original.Token, runtime).AsTask());
        else if (mode == "end-failure") await Assert.ThrowsAsync<IOException>(() => h.CallRequest(request, Next, original.Token, runtime).AsTask());
        else
        {
            var result = await h.CallRequest(request, Next, original.Token, runtime);
            Assert.Equal(mode != "success", result.IsError == true);
        }
        Assert.Same(prior, request.Items[key]);
        Assert.Equal(mode is "success" or "cancel" or "exception" or "end-failure", nextCalled);
        if (mode is "cancel" or "exception")
        {
            Assert.Equal("failed", h.Text("outcome"));
            Assert.Equal("String(6) ", h.Text("output_preview"));
        }
    }

    [Fact]
    public async Task Simultaneous_recorder_requests_keep_distinct_items_and_original_tokens()
    {
        await using var h = new Harness();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var a = h.Request();
        var b = h.Request();
        var aEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<CallToolResult> Next(RequestContext<CallToolRequestParams> context, CancellationToken execution)
        {
            var entered = ReferenceEquals(context, a) ? aEntered : bEntered;
            try
            {
                Assert.Equal(ReferenceEquals(context, a) ? first.Token : second.Token, McpInvocation.RequireOriginalWaitToken(context));
                Assert.Equal(CancellationToken.None, execution);
                entered.TrySetResult();
                await release.Task;
                Assert.Equal(ReferenceEquals(context, a), McpInvocation.RequireOriginalWaitToken(context).IsCancellationRequested);
                return Answer();
            }
            catch (Exception error) { entered.TrySetException(error); throw; }
        }
        var calls = new[] { h.CallRequest(a, Next, first.Token).AsTask(), h.CallRequest(b, Next, second.Token).AsTask() };
        try
        {
            await Task.WhenAll(aEntered.Task, bEntered.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotSame(a.Items, b.Items);
            first.Cancel();
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.False(a.Items.ContainsKey("assembly.internal.original-request-wait/v1"));
        Assert.False(b.Items.ContainsKey("assembly.internal.original-request-wait/v1"));
        Assert.Equal(2, h.Fixture.Count("engine_tool_call"));
        Assert.Equal(2, h.Fixture.Number("SELECT COUNT(*) FROM engine_tool_call WHERE outcome='ok'"));
    }

    private static long McpCalls(UsageTotals totals) => JsonSerializer.SerializeToElement(totals,
        EngineSocketJson.Default.UsageTotals).GetProperty("mcpCalls").GetInt64();

    private sealed class FailingRuntime(IEngineRuntime inner, int failAt) : IEngineRuntime
    {
        private int _writes;
        public string Machine => inner.Machine;
        public EngineCommands Commands => inner.Commands;
        public T Read<T>(Func<IReadScope, T> body, CancellationToken token) => inner.Read(body, token);
        public T Write<T>(Func<IWriteScope, T> body)
        {
            if (++_writes != failAt) return inner.Write(body);
            throw new IOException("secret storage failure");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_recording_failure_never_runs_tool(bool refuse)
    {
        await using var h = new Harness();
        var result = await h.Call((_, _) => throw new InvalidOperationException("must not execute"), runtime: refuse ? null : new FailingRuntime(h.Runtime, 1), name: refuse ? "" : "probe");
        Assert.True(result.IsError);
        Assert.Contains("could not record", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(0, h.Fixture.Count("engine_tool_call"));
        Assert.Equal(0, h.Fixture.Count("engine_event"));
    }

    [Fact]
    public async Task End_recording_failure_is_not_reported_as_success()
    {
        await using var h = new Harness();
        await Assert.ThrowsAsync<IOException>(() => h.Call((_, _) => ValueTask.FromResult(Answer()), runtime: new FailingRuntime(h.Runtime, 2)));
        Assert.Null(h.Text("ended_ms"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Worker_call_is_attributed_to_its_run_and_step(bool sessionRun, bool stepSession)
    {
        using var f = new EngineFixture();
        f.SeedSession("owner"); f.SeedRun(ownerSessionId: "owner");
        f.SeedSession("worker", parentId: "owner", runId: sessionRun ? "r" : null);
        f.SeedStep("st", sessionId: stepSession ? "worker" : "owner", attempt: 2);
        var a = new Attribution("worker", "r", "st", 2, 0);
        Assert.True(f.Apply(new ToolStarted(a, "mcp", "call", "probe", JsonSerializer.SerializeToElement(0), 1), "call", actor: Recorder).Applied);
        Assert.Equal("worker|r|st|2", f.Text("SELECT session_id||'|'||run_id||'|'||step_id||'|'||attempt FROM engine_tool_call"));
    }

    [Fact]
    public void Unrelated_session_cannot_attribute_to_a_run()
    {
        using var f = new EngineFixture();
        f.SeedSession(); f.SeedRun(); f.SeedStep("st", sessionId: "s"); f.SeedSession("unrelated");
        var a = new Attribution("unrelated", "r", "st", 0, 0);
        Assert.Equal("attribution_mismatch", f.Apply(new ToolStarted(a, "mcp", "call", "probe", JsonSerializer.SerializeToElement(0), 1), "call", actor: Recorder).Error);
        Assert.Equal(0, f.Count("engine_tool_call"));
    }
}
