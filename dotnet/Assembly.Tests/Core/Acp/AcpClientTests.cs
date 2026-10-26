using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Remote.Smoke;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Tests.Core.Acp;

public sealed class AcpClientTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static AcpClient Connect(FakeAgent agent, IPermissionPolicy? permissions = null) =>
        new(agent.ClientReads, agent.ClientWrites, permissions ?? new AllowOncePolicy(), TranscriptLog.Discard());

    [Fact]
    public async Task Initialize_offers_protocol_1_and_no_client_capabilities()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent);

        var call = client.InitializeAsync(new Implementation("test", "1"), null, CancellationToken.None);
        var request = await agent.ReadAsync();

        Assert.Equal("initialize", request.GetProperty("method").GetString());
        var p = request.GetProperty("params");
        Assert.Equal(1, p.GetProperty("protocolVersion").GetInt32());
        Assert.False(p.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.False(p.GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean());
        Assert.False(p.GetProperty("clientCapabilities").TryGetProperty("_meta", out _));
        Assert.Equal("{}", p.GetProperty("clientCapabilities").GetProperty("subagents").GetRawText());

        await agent.ReplyAsync(request, """{"protocolVersion":1,"agentCapabilities":{},"authMethods":[],"agentInfo":{"name":"fake","version":"9"}}""");

        var response = await call.WaitAsync(Wait);
        Assert.Equal(1, response.ProtocolVersion);
        Assert.Equal("fake", response.AgentInfo?.Name);
    }

    [Fact]
    public async Task Initialize_opts_into_the_AIR_extensions_it_is_given()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent);

        var call = client.InitializeAsync(new Implementation("test", "1"), ["sessionIndex"], CancellationToken.None);
        var request = await agent.ReadAsync();

        var air = request.GetProperty("params").GetProperty("clientCapabilities").GetProperty("_meta").GetProperty("jetbrains").GetProperty("air");
        Assert.Equal(1, air.GetProperty("version").GetInt32());
        Assert.Equal(["sessionIndex"], air.GetProperty("capabilities").EnumerateArray().Select(capability => capability.GetString()));
        await agent.ReplyAsync(request, """{"protocolVersion":1,"agentCapabilities":{},"authMethods":[],"_meta":{"jetbrains":{"air":{"version":1,"capabilities":["sessionIndex","sessionRename"]}}}}""");
        Assert.True((await call.WaitAsync(Wait)).CanRenameSessions);
    }

    [Fact]
    public async Task Prompt_answers_a_permission_request_and_streams_updates()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent);

        var turn = client.PromptAsync("s1", "hi", CancellationToken.None);
        var prompt = await agent.ReadAsync();
        Assert.Equal("session/prompt", prompt.GetProperty("method").GetString());
        Assert.Equal("hi", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());

        await agent.SendAsync("""{"jsonrpc":"2.0","id":"p1","method":"session/request_permission","params":{"sessionId":"s1","toolCall":{"toolCallId":"t1","title":"echo hi"},"options":[{"optionId":"no","name":"Reject","kind":"reject_once"},{"optionId":"yes","name":"Allow","kind":"allow_once"}]}}""");
        var answer = await agent.ReadAsync();
        Assert.Equal("p1", answer.GetProperty("id").GetString());
        var outcome = answer.GetProperty("result").GetProperty("outcome");
        Assert.Equal("selected", outcome.GetProperty("outcome").GetString());
        Assert.Equal("yes", outcome.GetProperty("optionId").GetString());

        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hi"}}}}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");

        Assert.Equal("end_turn", (await turn.WaitAsync(Wait)).StopReason);
        var update = await client.Updates.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal("agent_message_chunk", update.Kind);
    }

    [Fact]
    public async Task Cancel_settles_an_open_permission_request_as_cancelled()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent, new WaitUntilCancelledPolicy());

        var turn = client.PromptAsync("s1", "hi", CancellationToken.None);
        var prompt = await agent.ReadAsync();
        await agent.SendAsync("""{"jsonrpc":"2.0","id":5,"method":"session/request_permission","params":{"sessionId":"s1","toolCall":{"toolCallId":"t1"},"options":[{"optionId":"yes","name":"Allow","kind":"allow_once"}]}}""");

        await client.CancelAsync("s1", CancellationToken.None);
        var lines = new[] { await agent.ReadAsync(), await agent.ReadAsync() };

        Assert.Contains(lines, l => l.TryGetProperty("method", out var m) && m.GetString() == "session/cancel");
        var answer = Assert.Single(lines, l => l.TryGetProperty("result", out _));
        Assert.Equal(5, answer.GetProperty("id").GetInt32());
        Assert.Equal("cancelled", answer.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());

        await agent.ReplyAsync(prompt, """{"stopReason":"cancelled"}""");
        Assert.Equal("cancelled", (await turn.WaitAsync(Wait)).StopReason);
    }

    [Fact]
    public async Task Methods_we_did_not_offer_get_method_not_found()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent);

        await agent.SendAsync("""{"jsonrpc":"2.0","id":7,"method":"fs/read_text_file","params":{"sessionId":"s1","path":"/etc/hosts"}}""");
        var answer = await agent.ReadAsync();

        Assert.Equal(7, answer.GetProperty("id").GetInt32());
        Assert.Equal(-32601, answer.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Agent_closing_fails_pending_requests_and_ends_updates()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent);

        var call = client.NewSessionAsync("/tmp", CancellationToken.None);
        await agent.ReadAsync();
        agent.CloseOutput();

        await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(Wait));
        await client.Updates.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task Agent_errors_come_back_as_JsonRpcException()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent);

        var call = client.NewSessionAsync("/tmp", CancellationToken.None);
        var request = await agent.ReadAsync();
        await agent.ReplyErrorAsync(request, -32000, "Authentication required");

        var error = await Assert.ThrowsAsync<Assembly.Application.Common.Acp.JsonRpc.JsonRpcException>(() => call.WaitAsync(Wait));
        Assert.Equal(-32000, error.Code);
        Assert.Equal("Authentication required", error.Message);
    }
}
