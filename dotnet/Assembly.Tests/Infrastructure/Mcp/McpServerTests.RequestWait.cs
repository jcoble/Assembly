using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assembly.Tests.Infrastructure.Mcp;

public sealed partial class McpServerTests
{
    [Fact]
    public async Task Official_http_sdk_request_keeps_original_wait_cancelable_and_execution_none()
    {
        var probe = new WaitTokenProbe();
        await using var h = await Host.StartAsync(extraTool: probe);
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(h.Http.BaseAddress!, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + h.Token }
        }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CallToolResult? result = null;
        Exception? invocationFailure = null;
        try { result = await client.CallToolAsync("wait_token_probe", cancellationToken: deadline.Token); }
        catch (Exception error) { invocationFailure = error; }
        // Missing capture completes as an actual SDK error, so RED cannot hang waiting for a success-only event.
        Assert.Null(probe.Failure);
        Assert.Null(invocationFailure);
        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Equal("{\"exact\":\"界\"}", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(new McpCaller("s", 7), probe.Caller);
        Assert.True(probe.Original.CanBeCanceled);
        Assert.Equal(CancellationToken.None, probe.Execution);
        Assert.NotNull(probe.Request);
        Assert.False(probe.Request.Items.ContainsKey("assembly.internal.original-request-wait/v1"));
        Assert.Equal("ok", h.Fixture.Text("SELECT outcome FROM engine_tool_call WHERE tool='wait_token_probe'"));
    }

    [Fact]
    public async Task Real_http_abort_cancels_original_wait_and_cleans_capture_after_recorder_exit()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var emergency = new CancellationTokenSource();
        var probe = new WaitTokenProbe(async original =>
        {
            entered.TrySetResult(original);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(original, emergency.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, wait.Token);
            throw new InvalidOperationException("The original request wait unexpectedly completed without cancellation.");
        });
        await using var h = await Host.StartAsync(extraTool: probe, requestFinished: context =>
        {
            if (context.Request.Headers.ContainsKey("X-R6-Cancel-Probe")) serverFinished.TrySetResult();
        });
        using var gone = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"wait_token_probe\",\"arguments\":{}}}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", h.Token);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("X-R6-Cancel-Probe", "1");
        var call = h.Http.SendAsync(request, gone.Token);
        try
        {
            var observed = await Task.WhenAny(entered.Task, call).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(probe.Failure);
            Assert.Same(entered.Task, observed);
            var original = await entered.Task;
            Assert.True(original.CanBeCanceled);
            Assert.Equal(CancellationToken.None, probe.Execution);
            gone.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await call; });
            await serverFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(original.IsCancellationRequested);
            Assert.False(probe.Execution.IsCancellationRequested);
            Assert.NotNull(probe.Request);
            Assert.False(probe.Request.Items.ContainsKey("assembly.internal.original-request-wait/v1"));
            Assert.Equal("failed", h.Fixture.Text("SELECT outcome FROM engine_tool_call WHERE tool='wait_token_probe'"));
            Assert.NotNull(h.Fixture.Text("SELECT ended_ms FROM engine_tool_call WHERE tool='wait_token_probe'"));
            Assert.Equal("String(6) ", h.Fixture.Text("SELECT output_preview FROM engine_tool_call WHERE tool='wait_token_probe'"));
        }
        finally
        {
            // Test-owned escape hatch guarantees a broken transport/capture fixture cannot strand server shutdown.
            emergency.Cancel();
            gone.Cancel();
            try { using var response = await call; }
            catch (OperationCanceledException) { }
            await serverFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class WaitTokenProbe(Func<CancellationToken, ValueTask<CallToolResult>>? respond = null) : McpServerTool
    {
        public RequestContext<CallToolRequestParams>? Request;
        public McpCaller? Caller;
        public CancellationToken Original;
        public CancellationToken Execution;
        public Exception? Failure;
        public override Tool ProtocolTool { get; } = new()
        {
            Name = "wait_token_probe",
            Description = "Test the original request lifetime.",
            InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"additionalProperties\":false}").RootElement.Clone()
        };
        public override IReadOnlyList<object> Metadata { get; } = [];
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default) =>
            McpInvocation.InvokeAsync(ProtocolTool.Name, request, (caller, _, execution) =>
            {
                Request = request;
                Caller = caller;
                Execution = execution;
                try
                {
                    Original = McpInvocation.RequireOriginalWaitToken(request);
                    return respond is null ? ValueTask.FromResult(McpInvocation.Text("{\"exact\":\"界\"}", false)) : respond(Original);
                }
                catch (Exception error) { Failure = error; throw; }
            }, cancellationToken);
    }
}
