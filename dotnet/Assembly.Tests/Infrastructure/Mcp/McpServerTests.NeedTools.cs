using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Assembly.Tests.Infrastructure.Mcp;

public sealed partial class McpServerTests
{
    [Fact]
    public async Task Registered_need_pair_answers_and_escalates_actual_addressed_child_over_official_sdk()
    {
        await using var h = await Host.StartAsync(needTools: true);
        var listed = (await h.CallAsync("tools/list", "{}")).GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Equal(["answer_need", "ask_owner", "list_sessions"], listed.Order(StringComparer.Ordinal));
        foreach (var id in new[] { "answer", "escalate" })
            Assert.True(h.Fixture.Apply(new OpenNeed("child", null, null, "question", "Proceed?", [], null, null, null),
                id, actor: new(ActorKind.System, "adapter")).Applied);
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(h.Http.BaseAddress!, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + h.Token }
        }));
        var answered = await client.CallToolAsync("answer_need", new Dictionary<string, object?> { ["needId"] = "answer", ["answer"] = "yes" });
        Assert.False(answered.IsError);
        Assert.Equal("{\"needId\":\"answer\",\"state\":\"answered\"}", Assert.IsType<TextContentBlock>(Assert.Single(answered.Content)).Text);
        Assert.Equal("answered", h.Fixture.State("engine_needs_me", "answer"));
        Assert.Equal("\"yes\"", h.Fixture.Text("SELECT answer_json FROM engine_needs_me WHERE id='answer'"));
        Assert.Equal("s", h.Fixture.Text("SELECT answered_by FROM engine_needs_me WHERE id='answer'"));
        var escalated = await client.CallToolAsync("ask_owner", new Dictionary<string, object?> { ["childNeedId"] = "escalate" });
        Assert.False(escalated.IsError);
        Assert.Equal("{\"needId\":\"escalate\",\"state\":\"open\"}", Assert.IsType<TextContentBlock>(Assert.Single(escalated.Content)).Text);
        Assert.Equal("open", h.Fixture.State("engine_needs_me", "escalate"));
        Assert.Null(h.Fixture.Text("SELECT addressee_session_id FROM engine_needs_me WHERE id='escalate'"));
        Assert.Equal(2, h.Fixture.Count("engine_needs_me"));
        Assert.Equal(2, h.Fixture.Number("SELECT COUNT(*) FROM engine_tool_call WHERE tool IN ('answer_need','ask_owner') AND outcome='ok' AND ended_ms IS NOT NULL"));
    }

    [Theory]
    [InlineData("answer_need", "{\"needId\":\"remote-need\",\"answer\":\"yes\"}")]
    [InlineData("ask_owner", "{\"childNeedId\":\"remote-need\"}")]
    public async Task Actual_need_tool_http_abort_detaches_wait_and_retains_one_committed_source(string tool, string arguments)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = await Host.StartAsync(needTools: true, requestFinished: context =>
        {
            if (context.Request.Headers.ContainsKey("X-Need-Cancel")) ended.TrySetResult();
        });
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = h.Runtime.Publisher.Register(item =>
        {
            if (item.Kind == "parent_need.requested") committed.TrySetResult();
        }, out _);
        using var gone = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", h.Token);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("X-Need-Cancel", "1");
        var call = h.Http.SendAsync(request, gone.Token);
        try
        {
            var first = await Task.WhenAny(committed.Task, call).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(committed.Task, first);
            Assert.False(call.IsCompleted);
            gone.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await call; });
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, h.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='parent_need' AND state='pending' AND attempt=0"));
            var id = h.Fixture.Text("SELECT id FROM engine_outbox WHERE kind='parent_need'")!;
            var payload = h.Runtime.Reader.ParentNeedInput(id, default).ReadPayload();
            Assert.Equal(ParentNeedPhase.Raw, payload.Phase);
            Assert.Equal(tool == "answer_need" ? ParentNeedAction.Answer : ParentNeedAction.Escalate, payload.Source.Action);
            Assert.Null(payload.Prepared); Assert.Null(payload.Outcome);
            Assert.Equal("failed", h.Fixture.Text("SELECT outcome FROM engine_tool_call WHERE tool=$tool", new SqlArg("$tool", tool)));
            Assert.NotNull(h.Fixture.Text("SELECT ended_ms FROM engine_tool_call WHERE tool=$tool", new SqlArg("$tool", tool)));
            Assert.Equal(0, h.Fixture.Count("engine_needs_me"));
        }
        finally
        {
            gone.Cancel();
            await h.Needs!.StopWaiting();
            try { using var response = await call; }
            catch (OperationCanceledException) { }
        }
    }
}
