using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Transcript;
using Assembly.Remote.Smoke;

namespace Assembly.Tests.Core.Acp;

/// <summary>
/// What a native subagent's child session asks (Claude's Agent tool) is asked under its parent's session, for as long
/// as the child is at work, and is settled as cancelled when the child, the parent's session or the agent ends.
/// </summary>
public sealed class AcpClientChildTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // In the order a real Claude subagent sent them (notes/sa0, nomcp-…162741-965-claude.jsonl lines 29-30 and 54-55):
    // the parent announces the child, the child gets its prompt, and then the child asks.
    private const string Announced =
        """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"parent","update":{"sessionUpdate":"subagent_update","sessionId":"child1","title":"general-purpose","description":"Count files","capabilities":{"cancel":{}},"state":{"state":"running"}}}}""";

    private const string ChildPrompt =
        """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"child1","update":{"sessionUpdate":"session_message","messageId":"toolu_1","content":[{"type":"text","text":"Count the files"}]}}}""";

    private const string ChildIdle =
        """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"parent","update":{"sessionUpdate":"subagent_update","sessionId":"child1","state":{"state":"idle","stopReason":"end_turn"}}}}""";

    private static string Ask(string sessionId) =>
        $$$"""{"jsonrpc":"2.0","id":9,"method":"session/request_permission","params":{"sessionId":"{{{sessionId}}}","toolCall":{"toolCallId":"t1","title":"find /usr/share/doc"},"options":[{"optionId":"no","name":"No","kind":"reject_once"},{"optionId":"yes","name":"Yes","kind":"allow_once"}]}}""";

    private static AcpClient Connect(FakeAgent agent) =>
        new(agent.ClientReads, agent.ClientWrites, new UnclaimedPermissions(), TranscriptLog.Discard());

    private static string Outcome(JsonElement answer)
    {
        var outcome = answer.GetProperty("result").GetProperty("outcome");
        return outcome.TryGetProperty("optionId", out var option) ? option.GetString()! : outcome.GetProperty("outcome").GetString()!;
    }

    [Fact]
    public async Task A_child_s_permission_request_during_its_parent_s_turn_is_asked_under_the_parent_s_session()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent);
        var parent = new AllowOncePolicy();
        client.SetSessionPolicy("parent", parent);
        var turn = client.PromptAsync("parent", "count them with a sub-agent", CancellationToken.None);
        var prompt = await agent.ReadAsync();

        await agent.SendAsync(Announced);
        await agent.SendAsync(ChildPrompt);
        await agent.SendAsync(Ask("child1"));

        Assert.Equal("yes", Outcome(await agent.ReadAsync()));
        Assert.Equal(["find /usr/share/doc"], parent.Asked);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await turn.WaitAsync(Wait);
    }

    [Fact]
    public async Task A_child_still_at_work_after_its_parent_s_turn_ended_is_still_asked()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent);
        var parent = new AllowOncePolicy();
        client.SetSessionPolicy("parent", parent);
        var turn = client.PromptAsync("parent", "count them in the background", CancellationToken.None);
        var prompt = await agent.ReadAsync();
        await agent.SendAsync(Announced);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await turn.WaitAsync(Wait);

        await agent.SendAsync(Ask("child1"));

        Assert.Equal("yes", Outcome(await agent.ReadAsync()));
        Assert.Equal(["find /usr/share/doc"], parent.Asked);
    }

    [Fact]
    public async Task A_request_from_a_session_nobody_announced_is_still_cancelled()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent);
        var parent = new AllowOncePolicy();
        client.SetSessionPolicy("parent", parent);
        var turn = client.PromptAsync("parent", "hi", CancellationToken.None);
        var prompt = await agent.ReadAsync();
        await agent.SendAsync(Announced);

        await agent.SendAsync(Ask("stranger"));

        Assert.Equal("cancelled", Outcome(await agent.ReadAsync()));
        Assert.Empty(parent.Asked);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await turn.WaitAsync(Wait);
    }

    [Fact]
    public async Task An_update_that_names_no_state_leaves_an_idle_child_idle()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent);
        var parent = new AllowOncePolicy();
        client.SetSessionPolicy("parent", parent);
        var turn = client.PromptAsync("parent", "count them with a sub-agent", CancellationToken.None);
        var prompt = await agent.ReadAsync();
        await agent.SendAsync(Announced);
        await agent.SendAsync(ChildIdle);

        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"parent","update":{"sessionUpdate":"subagent_update","sessionId":"child1","title":"renamed"}}}""");
        await agent.SendAsync(Ask("child1"));

        Assert.Equal("cancelled", Outcome(await agent.ReadAsync()));
        Assert.Empty(parent.Asked);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await turn.WaitAsync(Wait);
    }

    // A child asks after its parent's turn has ended, and nobody answers: each way the child's work can end settles it.
    private static async Task<(FakeAgent Agent, AcpClient Client, HeldPolicy Parent)> AskedAfterTheTurnAsync()
    {
        var agent = new FakeAgent();
        var client = Connect(agent);
        var parent = new HeldPolicy();
        client.SetSessionPolicy("parent", parent);
        var turn = client.PromptAsync("parent", "count them in the background", CancellationToken.None);
        var prompt = await agent.ReadAsync();
        await agent.SendAsync(Announced);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await turn.WaitAsync(Wait);
        await agent.SendAsync(Ask("child1"));
        await parent.Asked.Task.WaitAsync(Wait);
        return (agent, client, parent);
    }

    [Fact]
    public async Task A_child_s_open_ask_is_cancelled_when_the_child_goes_idle()
    {
        var (agent, client, _) = await AskedAfterTheTurnAsync();
        await using var _client = client;

        await agent.SendAsync(ChildIdle);

        Assert.Equal("cancelled", Outcome(await agent.ReadAsync().WaitAsync(Wait)));
    }

    [Fact]
    public async Task A_child_s_open_ask_is_cancelled_when_its_parent_s_session_closes()
    {
        var (agent, client, _) = await AskedAfterTheTurnAsync();
        await using var _client = client;

        client.SetSessionPolicy("parent", null);

        Assert.Equal("cancelled", Outcome(await agent.ReadAsync().WaitAsync(Wait)));
    }

    [Fact]
    public async Task A_child_s_open_ask_is_cancelled_when_the_agent_goes()
    {
        var (agent, client, parent) = await AskedAfterTheTurnAsync();
        await using var _client = client;

        agent.CloseOutput();

        await parent.Cancelled.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task Cancelling_the_parent_s_turn_settles_its_child_s_open_ask()
    {
        var agent = new FakeAgent();
        await using var client = Connect(agent);
        var parent = new HeldPolicy();
        client.SetSessionPolicy("parent", parent);
        var turn = client.PromptAsync("parent", "count them with a sub-agent", CancellationToken.None);
        var prompt = await agent.ReadAsync();
        await agent.SendAsync(Announced);
        await agent.SendAsync(Ask("child1"));
        await parent.Asked.Task.WaitAsync(Wait);

        await client.CancelAsync("parent", CancellationToken.None);

        var lines = new[] { await agent.ReadAsync(), await agent.ReadAsync() };
        Assert.Equal("cancelled", Outcome(Assert.Single(lines, line => line.TryGetProperty("result", out _))));
        await agent.ReplyAsync(prompt, """{"stopReason":"cancelled"}""");
        await turn.WaitAsync(Wait);
    }

    // A user who never answers; says when it was asked and when the ask was cancelled.
    private sealed class HeldPolicy : IPermissionPolicy
    {
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
        {
            Asked.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Cancelled.TrySetResult();
            }
            return PermissionOutcome.Cancelled;
        }
    }
}
