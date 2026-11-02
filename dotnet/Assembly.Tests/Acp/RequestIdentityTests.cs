using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Tests.Core.Acp;

namespace Assembly.Tests.Acp;

public sealed class RequestIdentityTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private sealed class Policy : IPermissionPolicy
    {
        public readonly Channel<(JsonRpcRequestContext Context, TaskCompletionSource<PermissionOutcome> Answer)> Requests = Channel.CreateUnbounded<(JsonRpcRequestContext, TaskCompletionSource<PermissionOutcome>)>();
        public readonly Channel<(JsonRpcRequestContext Context, JsonElement Outcome)> Decisions = Channel.CreateUnbounded<(JsonRpcRequestContext, JsonElement)>();
        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken ct) => throw new InvalidOperationException("Context was erased");
        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, JsonRpcRequestContext context, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<PermissionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Writer.TryWrite((context, answer));
            return answer.Task.WaitAsync(ct);
        }
        public void Decided(string method, string acpSessionId, JsonRpcRequestContext context, JsonElement outcome) => Decisions.Writer.TryWrite((context, outcome));
    }

    [Fact]
    public async Task Numeric_and_string_ids_survive_disposed_documents_concurrent_reverse_answers_and_connections()
    {
        var policy = new Policy(); var first = new FakeAgent(); var second = new FakeAgent();
        await using var c1 = new AcpClient(first.ClientReads, first.ClientWrites, policy, TranscriptLog.Discard());
        await using var c2 = new AcpClient(second.ClientReads, second.ClientWrites, policy, TranscriptLog.Discard());
        var p1 = c1.PromptAsync("s", [TextBlock.Of("hi")], default); var r1 = await first.ReadAsync();
        var p2 = c2.PromptAsync("s", [TextBlock.Of("hi")], default); var r2 = await second.ReadAsync();
        string Request(string id) => """{"jsonrpc":"2.0","id":ID,"method":"session/request_permission","params":{"sessionId":"s","title":"Run?","options":[{"optionId":"yes","name":"Yes","kind":"allow_once"}]}}""".Replace("ID", id);
        await first.SendAsync(Request("7")); await first.SendAsync(Request("\"7\"")); await second.SendAsync(Request("7"));
        var a = await policy.Requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        var b = await policy.Requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        var d = await policy.Requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        var entries = new[] { a, b, d };
        Assert.Equal(3, entries.Select(e => e.Context.ProviderRequestId).Distinct().Count());
        Assert.Equal(2, entries.Select(e => e.Context.ConnectionId).Distinct().Count());
        Assert.Contains(entries, e => e.Context.Id.ValueKind == JsonValueKind.String);
        // Cloned envelope IDs remain valid well after the reader disposed the original documents.
        foreach (var entry in entries.Reverse()) entry.Answer.SetResult(PermissionOutcome.Selected("yes"));
        var replies = new[] { await first.ReadAsync(), await first.ReadAsync() };
        Assert.Contains(replies, r => r.GetProperty("id").ValueKind == JsonValueKind.String);
        Assert.Contains(replies, r => r.GetProperty("id").ValueKind == JsonValueKind.Number);
        Assert.Equal(7, (await second.ReadAsync()).GetProperty("id").GetInt32());
        foreach (var unused in entries)
        {
            var decision = await policy.Decisions.Reader.ReadAsync().AsTask().WaitAsync(Limit);
            Assert.Equal("selected", decision.Outcome.GetProperty("outcome").GetString());
        }
        await Finish(first, r1); await Finish(second, r2);
        await p1.Ended.WaitAsync(Limit); await p1.Inserted.WaitAsync(Limit);
        await p2.Ended.WaitAsync(Limit); await p2.Inserted.WaitAsync(Limit);
        first.CloseOutput(); second.CloseOutput();
    }

    [Fact]
    public async Task Board_policy_keeps_automatic_wire_answer_and_reports_actual_decision_without_a_manual_ask()
    {
        var policy = new Policy(); var agent = new FakeAgent();
        await using var client = new AcpClient(agent.ClientReads, agent.ClientWrites, policy, TranscriptLog.Discard());
        var pending = client.PromptAsync("s", [TextBlock.Of("hi")], default); var prompt = await agent.ReadAsync();
        await agent.SendAsync("""{"jsonrpc":"2.0","id":"auto","method":"session/request_permission","params":{"sessionId":"s","title":"mcp__assembly__board_update","options":[{"optionId":"allow","name":"Allow","kind":"allow_once"}]}}""");
        var reply = await agent.ReadAsync();
        Assert.Equal("allow", reply.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
        Assert.False(policy.Requests.Reader.TryRead(out _));
        var decision = await policy.Decisions.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        Assert.Equal("\"auto\"", decision.Context.Id.GetRawText());
        Assert.Equal("allow", decision.Outcome.GetProperty("optionId").GetString());
        await Finish(agent, prompt); await pending.Inserted.WaitAsync(Limit); await pending.Ended.WaitAsync(Limit); agent.CloseOutput();
    }

    private static async Task Finish(FakeAgent agent, JsonElement prompt)
    {
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"user_message","messageId":"m"}}}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"state_update","state":"running"}}}""");
        await agent.ReplyAsync(prompt, """{"messageId":"m"}""");
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}}}""");
    }
}
