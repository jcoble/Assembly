using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Tests.Core.Acp;

namespace Assembly.Tests.Acp;

public sealed class NativePolicyTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private sealed class Policy : IPermissionPolicy
    {
        public readonly Channel<string> Cancels = Channel.CreateUnbounded<string>();
        public void Cancelling(string actualAcpSessionId) => Cancels.Writer.TryWrite(actualAcpSessionId);
        public readonly Channel<(string Session, JsonRpcRequestContext Context, TaskCompletionSource<PermissionOutcome> Answer)> Requests = Channel.CreateUnbounded<(string, JsonRpcRequestContext, TaskCompletionSource<PermissionOutcome>)>();
        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken ct) => Task.FromResult(PermissionOutcome.Cancelled);
        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, JsonRpcRequestContext context, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<PermissionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Writer.TryWrite((request.SessionId, context, answer));
            return answer.Task.WaitAsync(ct);
        }
    }
    private static string Member(string parent, string child) => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = parent, update = new { sessionUpdate = "subagent_update", sessionId = child, title = "Review", state = new { state = "running" } } } });
    private static string Ask(string child, int id) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "session/request_permission", @params = new { sessionId = child, title = "Read?", options = new[] { new { optionId = "yes", name = "Yes", kind = "allow_once" } } } });

    [Fact]
    public async Task Immediately_preceding_membership_binds_child_and_each_request_retains_its_arrival_policy()
    {
        var agent = new FakeAgent(); var first = new Policy(); var replacement = new Policy();
        await using var client = new AcpClient(agent.ClientReads, agent.ClientWrites, new Policy(), TranscriptLog.Discard());
        client.SetSessionPolicy("parent", first);
        var prompt = client.PromptAsync("parent", [TextBlock.Of("hi")], default); var request = await agent.ReadAsync();
        await agent.SendAsync(Member("parent", "child") + "\n" + Ask("child", 7));
        var old = await first.Requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        Assert.Equal("child", old.Session);
        client.SetSessionPolicy("parent", replacement);
        await agent.SendAsync(Ask("child", 8));
        var current = await replacement.Requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        current.Answer.SetResult(PermissionOutcome.Selected("new"));
        old.Answer.SetResult(PermissionOutcome.Selected("old"));
        var replies = new[] { await agent.ReadAsync(), await agent.ReadAsync() };
        Assert.Equal("old", replies.Single(r => r.GetProperty("id").GetInt32() == 7).GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
        Assert.Equal("new", replies.Single(r => r.GetProperty("id").GetInt32() == 8).GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
        await End(agent, request, "parent", prompt);
        agent.CloseOutput();
    }

    [Fact]
    public async Task Conflicting_parents_deny_and_an_independent_root_policy_is_never_stolen()
    {
        var agent = new FakeAgent(); var p = new Policy(); var q = new Policy(); var root = new Policy();
        await using var client = new AcpClient(agent.ClientReads, agent.ClientWrites, new Policy(), TranscriptLog.Discard());
        client.SetSessionPolicy("p", p); client.SetSessionPolicy("q", q); client.SetSessionPolicy("root", root);
        var pp = client.PromptAsync("p", [TextBlock.Of("hi")], default); var pr = await agent.ReadAsync();
        var qp = client.PromptAsync("q", [TextBlock.Of("hi")], default); var qr = await agent.ReadAsync();
        var rp = client.PromptAsync("root", [TextBlock.Of("hi")], default); var rr = await agent.ReadAsync();
        await agent.SendAsync(Member("p", "child") + "\n" + Member("q", "child") + "\n" + Ask("child", 7));
        Assert.Equal("cancelled", (await agent.ReadAsync()).GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.False(p.Requests.Reader.TryRead(out _)); Assert.False(q.Requests.Reader.TryRead(out _));
        await agent.SendAsync(Member("p", "root") + "\n" + Member("p", "p") + "\n" + Ask("root", 8));
        var own = await root.Requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        own.Answer.SetResult(PermissionOutcome.Selected("root")); await agent.ReadAsync();
        Assert.False(p.Requests.Reader.TryRead(out _));
        await End(agent, pr, "p", pp); await End(agent, qr, "q", qp); await End(agent, rr, "root", rp); agent.CloseOutput();
    }

    [Fact]
    public async Task Parent_close_cancels_child_waiter_and_reopen_requires_fresh_membership()
    {
        var agent = new FakeAgent(); var policy = new Policy();
        await using var client = new AcpClient(agent.ClientReads, agent.ClientWrites, new Policy(), TranscriptLog.Discard());
        client.SetSessionPolicy("p", policy);
        var prompt = client.PromptAsync("p", [TextBlock.Of("hi")], default); var request = await agent.ReadAsync();
        await agent.SendAsync(Member("p", "c") + "\n" + Ask("c", 7));
        await policy.Requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        var closing = client.CloseSessionAsync("p", default); var close = await agent.ReadAsync();
        await agent.ReplyAsync(close, "{}"); await closing.WaitAsync(Limit);
        Assert.Equal("cancelled", (await agent.ReadAsync()).GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        await Assert.ThrowsAnyAsync<Exception>(() => prompt.Inserted.WaitAsync(Limit)); await Assert.ThrowsAnyAsync<Exception>(() => prompt.Ended.WaitAsync(Limit));
        client.SetSessionPolicy("p", policy);
        var reopened = client.PromptAsync("p", [TextBlock.Of("again")], default); var next = await agent.ReadAsync();
        await agent.SendAsync(Ask("c", 8));
        Assert.Equal("cancelled", (await agent.ReadAsync()).GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.False(policy.Requests.Reader.TryRead(out _));
        await agent.SendAsync(Member("p", "c") + "\n" + Ask("c", 9));
        var known = await policy.Requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        known.Answer.SetResult(PermissionOutcome.Selected("yes")); await agent.ReadAsync();
        await End(agent, next, "p", reopened); agent.CloseOutput();
    }

    [Fact]
    public async Task Child_cancel_uses_only_current_validated_membership_and_reopen_drops_old_claim()
    {
        var agent = new FakeAgent(); var old = new Policy(); var current = new Policy();
        await using var client = new AcpClient(agent.ClientReads, agent.ClientWrites, new Policy(), TranscriptLog.Discard());
        client.SetSessionPolicy("p", old);
        var prompt = client.PromptAsync("p", [TextBlock.Of("hi")], default); var request = await agent.ReadAsync();
        await agent.SendAsync(Member("p", "c") + "\n" + Ask("c", 7));
        var ask = await old.Requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        client.SetSessionPolicy("p", current);
        var cancel = client.CancelAsync("c", default); await agent.ReadAsync();
        Assert.Equal("c", await current.Cancels.Reader.ReadAsync().AsTask().WaitAsync(Limit));
        Assert.False(old.Cancels.Reader.TryRead(out _));
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"c","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}}}""");
        await cancel.WaitAsync(Limit);
        ask.Answer.SetResult(PermissionOutcome.Selected("yes")); await agent.ReadAsync();
        await End(agent, request, "p", prompt);
        var closing = client.CloseSessionAsync("p", default);
        await agent.ReplyAsync(await agent.ReadAsync(), "{}"); await closing.WaitAsync(Limit);
        client.SetSessionPolicy("p", current);
        var reopened = client.PromptAsync("p", [TextBlock.Of("again")], default); var next = await agent.ReadAsync();
        var stale = client.CancelAsync("c", default); await agent.ReadAsync();
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"c","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}}}""");
        await stale.WaitAsync(Limit);
        Assert.False(current.Cancels.Reader.TryRead(out _));
        await End(agent, next, "p", reopened); agent.CloseOutput();
    }

    private static async Task End(FakeAgent agent, JsonElement request, string session, PromptRun prompt)
    {
        string Update(object update) => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = session, update } });
        await agent.SendAsync(Update(new { sessionUpdate = "user_message", messageId = "m" }));
        await agent.SendAsync(Update(new { sessionUpdate = "state_update", state = "running" }));
        await agent.ReplyAsync(request, """{"messageId":"m"}""");
        await agent.SendAsync(Update(new { sessionUpdate = "state_update", state = "idle", stopReason = "end_turn" }));
        await prompt.Inserted.WaitAsync(Limit); await prompt.Ended.WaitAsync(Limit);
    }
}
