using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Tests.Core.Acp;

namespace Assembly.Tests.Acp;

public sealed class AcpBoardPermissionTests
{
    [Fact]
    public async Task Ordinary_policies_keep_the_existing_automatic_board_allow_once()
    {
        var agent = new FakeAgent();
        var policy = new DefaultPolicy();
        await using var client = new AcpClient(agent.ClientReads, agent.ClientWrites, policy, TranscriptLog.Discard());
        var run = client.PromptAsync("owned", [TextBlock.Of("Work")], default);
        await agent.ReplyAsync(await agent.ReadAsync(), """{"messageId":"input"}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"owned","update":{"sessionUpdate":"user_message_chunk","messageId":"input","content":{"type":"text","text":"Work"}}}}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"owned","update":{"sessionUpdate":"state_update","state":"running"}}}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","id":91,"method":"session/request_permission","params":{"sessionId":"owned","title":"mcp__assembly__board_update","options":[{"optionId":"allow","name":"Allow once","kind":"allow_once"}]}}""");
        var answer = await agent.ReadAsync();
        Assert.Equal(91, answer.GetProperty("id").GetInt32());
        var outcome = answer.GetProperty("result").GetProperty("outcome");
        Assert.Equal("selected", outcome.GetProperty("outcome").GetString());
        Assert.Equal("allow", outcome.GetProperty("optionId").GetString());
        Assert.Equal(0, policy.Decisions);
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"owned","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}}}""");
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class DefaultPolicy : IPermissionPolicy
    {
        internal int Decisions;
        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
        { Decisions++; return Task.FromResult(PermissionOutcome.Cancelled); }
    }
}
