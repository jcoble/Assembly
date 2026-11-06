using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Remote.Record;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Tests.Core.Acp;

public sealed class AcpClientTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static AcpClient Connect(FakeAgent agent, IPermissionPolicy? permissions = null) =>
        new(agent.ClientReads, agent.ClientWrites, permissions ?? new AllowOncePolicy(), TranscriptLog.Discard());

    [Fact]
    public async Task Initialize_offers_only_v2_and_refuses_a_v1_response()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var call = client.InitializeAsync(new("test", "1"), default);
        var request = await agent.ReadAsync();
        var parameters = request.GetProperty("params");
        Assert.Equal(2, parameters.GetProperty("protocolVersion").GetInt32());
        Assert.False(parameters.GetProperty("capabilities").TryGetProperty("_meta", out _));
        await agent.ReplyAsync(request, """{"protocolVersion":1,"capabilities":{}}""");
        Assert.Equal(-32022, (await Assert.ThrowsAsync<JsonRpcException>(() => call.WaitAsync(Wait))).Code);
    }

    [Fact]
    public async Task Resume_drops_replay_and_reopens_at_the_response_before_live_updates()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var resumed = false; client.SessionResumed += _ => resumed = true;
        var call = client.ResumeSessionAsync(new("s1", "/tmp", []), default);
        var request = await agent.ReadAsync();
        await agent.SendAsync(string.Join('\n', Text("before"),
            $$$"""{"jsonrpc":"2.0","id":{{{request.GetProperty("id").GetRawText()}}},"result":{}}""", Text("after")));
        await call.WaitAsync(Wait);
        var update = await client.Updates.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.True(resumed);
        Assert.Equal("after", update.Update.GetProperty("content").GetProperty("text").GetString());
        Assert.False(client.Updates.TryRead(out _));
    }

    private static string Text(string text) =>
        """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"agent_message_chunk","messageId":"m","content":{"type":"text","text":""" + JsonSerializer.Serialize(text) + "}}}}";

    [Fact]
    public async Task Permission_v2_reaches_policy_and_cancel_settles_it()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent, new WaitUntilCancelledPolicy());
        var run = client.PromptAsync("s1", [TextBlock.Of("hi")], default);
        var prompt = await agent.ReadAsync();
        await agent.SendAsync("""{"jsonrpc":"2.0","id":5,"method":"session/request_permission","params":{"sessionId":"s1","title":"Run echo","subject":{"type":"tool_call","toolCall":{"toolCallId":"t1"}},"options":[{"optionId":"yes","name":"Allow","kind":"allow_once"}]}}""");
        var cancelling = client.CancelAsync("s1", default);
        var lines = new[] { await agent.ReadAsync(), await agent.ReadAsync() };
        Assert.Contains(lines, l => l.TryGetProperty("method", out var m) && m.GetString() == "session/cancel");
        Assert.Equal("cancelled", Assert.Single(lines, l => l.TryGetProperty("result", out _))
            .GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"user_message","messageId":"m","content":[]}}}""");
        await agent.ReplyAsync(prompt, """{"messageId":"m"}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}}}""");
        await cancelling.WaitAsync(Wait);
        Assert.Equal("cancelled", (await run.Ended.WaitAsync(Wait)).StopReason);
    }

    [Fact]
    public async Task Unsupported_client_methods_get_method_not_found()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        await agent.SendAsync("""{"jsonrpc":"2.0","id":7,"method":"fs/read_text_file","params":{"sessionId":"s1","path":"/etc/hosts"}}""");
        Assert.Equal(-32601, (await agent.ReadAsync()).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Agent_closing_fails_pending_requests_and_ends_updates()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var call = client.NewSessionAsync(new("/tmp", []), default);
        await agent.ReadAsync(); agent.CloseOutput();
        await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(Wait));
        await client.Updates.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task Error_details_are_preserved_as_typed_data()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var call = client.NewSessionAsync(new("/tmp", []), default); var request = await agent.ReadAsync();
        await agent.SendAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.GetProperty("id").GetInt64(), error = new { code = -32000, message = "Sign in", data = new { details = "required" } } }));
        var error = await Assert.ThrowsAsync<JsonRpcException>(() => call.WaitAsync(Wait));
        Assert.True(error.Error.IsAuthRequired);
        Assert.Equal("required", error.Error.Data!.Value.GetProperty("details").GetString());
    }
}
