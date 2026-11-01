using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Infrastructure.Transcript;
using Assembly.Remote.Smoke;
using Assembly.Tests.Core.Acp;

namespace Assembly.Tests.Acp;

public sealed class AcpClientV2Tests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);
    private static AcpClient Connect(FakeAgent agent) =>
        new(agent.ClientReads, agent.ClientWrites, new AllowOncePolicy(), TranscriptLog.Discard());
    private static Task Update(FakeAgent agent, string update) =>
        agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s","update":UPDATE}}""".Replace("UPDATE", update));
    private static Task Ack(FakeAgent agent, string id) => Update(agent,
        """{"sessionUpdate":"user_message_chunk","messageId":"ID","content":{"type":"text","text":"hi"}}""".Replace("ID", id));
    private const string Running = """{"sessionUpdate":"state_update","state":"running"}""";
    private const string End = """{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""";

    [Fact]
    public async Task Prompt_completes_on_its_own_idle_when_updates_race_the_response()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var run = client.PromptAsync("s", [TextBlock.Of("hi")], default);
        var request = await agent.ReadAsync();
        await Ack(agent, "m-1"); await Update(agent, Running); await Update(agent, End);
        await agent.ReplyAsync(request, """{"messageId":"m-1"}""");
        var result = await run.Ended.WaitAsync(Bounded);
        Assert.Equal(("m-1", "end_turn"), (result.MessageId, result.StopReason));
        Assert.Equal("m-1", await run.Inserted.WaitAsync(Bounded));
    }

    [Fact]
    public async Task Two_prompts_complete_in_insertion_order()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var first = client.PromptAsync("s", [TextBlock.Of("first")], default);
        var r1 = await agent.ReadAsync();
        var second = client.PromptAsync("s", [TextBlock.Of("second")], default);
        var r2 = await agent.ReadAsync();
        await Ack(agent, "m-1"); await Update(agent, Running); await Update(agent, End);
        await Ack(agent, "m-2"); await Update(agent, Running); await Update(agent, End);
        // Reverse the responses to exercise the independent id bindings.
        await agent.ReplyAsync(r2, """{"messageId":"m-2"}""");
        await agent.ReplyAsync(r1, """{"messageId":"m-1"}""");
        Assert.Equal("m-1", (await first.Ended.WaitAsync(Bounded)).MessageId);
        Assert.Equal("m-2", (await second.Ended.WaitAsync(Bounded)).MessageId);
    }

    [Fact]
    public async Task Repeated_idle_and_running_before_ack_do_not_end_the_prompt()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var run = client.PromptAsync("s", [TextBlock.Of("hi")], default);
        var request = await agent.ReadAsync();
        await Update(agent, Running); await Ack(agent, "m");
        await Update(agent, """{"sessionUpdate":"state_update","state":"idle"}""");
        await agent.ReplyAsync(request, """{"messageId":"m"}""");
        await run.Inserted.WaitAsync(Bounded);
        Assert.False(run.Ended.IsCompleted);
        await Update(agent, Running); await Update(agent, End);
        Assert.Equal("end_turn", (await run.Ended.WaitAsync(Bounded)).StopReason);
    }

    [Fact]
    public async Task Error_stop_reason_is_a_result_not_an_exception()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var run = client.PromptAsync("s", [TextBlock.Of("hi")], default); var request = await agent.ReadAsync();
        await Ack(agent, "m");
        await Update(agent, """{"sessionUpdate":"state_update","state":"idle","stopReason":"error","error":{"code":-32603,"message":"oops"}}""");
        await agent.ReplyAsync(request, """{"messageId":"m"}""");
        Assert.Equal(-32603, (await run.Ended.WaitAsync(Bounded)).Error!.Code);
    }

    [Fact]
    public async Task Inserted_completes_at_the_response_and_faults_on_an_error_before_insertion()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var run = client.PromptAsync("s", [TextBlock.Of("hi")], default); var request = await agent.ReadAsync();
        await agent.ReplyErrorAsync(request, -32000, "Sign in");
        Assert.True((await Assert.ThrowsAsync<JsonRpcException>(() => run.Inserted.WaitAsync(Bounded))).Error.IsAuthRequired);
        await Assert.ThrowsAsync<JsonRpcException>(() => run.Ended.WaitAsync(Bounded));
    }

    [Fact]
    public async Task Steer_prompt_required_is_returned_without_waiting()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var call = client.SteerAsync("s", [TextBlock.Of("hi")], default); var request = await agent.ReadAsync();
        Assert.Equal("promptRequired", request.GetProperty("params").GetProperty("_meta").GetProperty("steering").GetProperty("idleBehavior").GetString());
        await agent.ReplyAsync(request, """{"outcome":"promptRequired"}""");
        Assert.Equal(SteerOutcome.PromptRequired, await call.WaitAsync(Bounded));
    }

    [Fact]
    public async Task Cancel_completes_on_idle_cancelled()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var call = client.CancelAsync("s", default);
        Assert.Equal("session/cancel", (await agent.ReadAsync()).GetProperty("method").GetString());
        Assert.False(call.IsCompleted);
        await Update(agent, """{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}""");
        await call.WaitAsync(Bounded);
    }

    [Fact]
    public async Task Resume_sends_no_replay_by_default_and_start_when_asked()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        foreach (var from in new ReplayFrom?[] { null, ReplayFrom.Start })
        {
            var call = client.ResumeSessionAsync(new("s", "/tmp", [], from), default); var request = await agent.ReadAsync();
            var parameters = request.GetProperty("params");
            Assert.Equal(from is not null, parameters.TryGetProperty("replayFrom", out var replay));
            if (from is not null) Assert.Equal("start", replay.GetProperty("type").GetString());
            await agent.ReplyAsync(request, "{}"); await call.WaitAsync(Bounded);
        }
    }

    [Theory]
    [InlineData("\"sessionId\":\"s\",\"requestId\":3,")]
    [InlineData("")]
    public async Task Elicitation_requires_exactly_one_id(string ids)
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        await agent.SendAsync("""{"jsonrpc":"2.0","id":7,"method":"elicitation/create","params":{IDS"mode":"form","requestedSchema":{}}}""".Replace("IDS", ids));
        Assert.Equal(-32602, (await agent.ReadAsync()).GetProperty("error").GetProperty("code").GetInt32());
    }
    [Fact]
    public async Task Resume_preserves_the_required_current_state_snapshot_before_live_traffic()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var call = client.ResumeSessionAsync(new("s", "/tmp", []), default); var request = await agent.ReadAsync();
        await Update(agent, Running);
        await agent.ReplyAsync(request, "{}"); await call.WaitAsync(Bounded);
        Assert.Equal("running", (await client.Updates.ReadAsync().AsTask().WaitAsync(Bounded)).Update.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Started_new_turn_is_a_protocol_error_with_prompt_required_steering()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        var call = client.SteerAsync("s", [TextBlock.Of("hi")], default); var request = await agent.ReadAsync();
        await agent.ReplyAsync(request, """{"outcome":"startedNewTurn"}""");
        await Assert.ThrowsAsync<JsonRpcException>(() => call.WaitAsync(Bounded));
    }

    [Fact]
    public async Task Agent_initiated_foreground_work_can_ask_permission_without_a_pending_prompt()
    {
        var agent = new FakeAgent(); await using var client = Connect(agent);
        await Update(agent, """{"sessionUpdate":"state_update","state":"requires_action"}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","id":9,"method":"session/request_permission","params":{"sessionId":"s","title":"Read output","subject":{"type":"command","command":"cat output.txt"},"options":[{"optionId":"yes","name":"Allow","kind":"allow_once"}]}}""");
        var response = await agent.ReadAsync();
        Assert.Equal("yes", response.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task Idle_settles_permission_even_when_the_prompt_response_is_late()
    {
        var agent = new FakeAgent();
        await using var client = new AcpClient(agent.ClientReads, agent.ClientWrites,
            new WaitUntilCancelledPolicy(), TranscriptLog.Discard());
        var run = client.PromptAsync("s", [TextBlock.Of("hi")], default);
        var request = await agent.ReadAsync();
        await Ack(agent, "m");
        await Update(agent, """{"sessionUpdate":"state_update","state":"requires_action"}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","id":9,"method":"session/request_permission","params":{"sessionId":"s","title":"Read","options":[{"optionId":"yes","name":"Allow","kind":"allow_once"}]}}""");
        await Update(agent, End);
        Assert.Equal("cancelled", (await agent.ReadAsync()).GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        await agent.ReplyAsync(request, """{"messageId":"m"}""");
        await run.Ended.WaitAsync(Bounded);
    }

}
