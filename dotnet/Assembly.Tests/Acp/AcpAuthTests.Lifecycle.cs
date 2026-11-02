using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Tests.Core.Acp;

namespace Assembly.Tests.Acp;

public sealed partial class AcpAuthTests
{
    private sealed class Target(Func<int> settled) : IJsonRpcTarget
    {
        public readonly Channel<int> Notifications = Channel.CreateUnbounded<int>();
        public Task<JsonElement> OnRequestAsync(string method, JsonElement parameters, CancellationToken ct) => Task.FromResult(parameters);
        public void OnNotification(string method, JsonElement parameters) => Notifications.Writer.TryWrite(settled());
        public void OnClosed(Exception reason) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outgoing_request_id_is_registered_before_write_and_terminal_fence_precedes_next_line(bool error)
    {
        var agent = new FakeAgent(); var settled = 0; var registered = 0L; var replied = 0;
        var target = new Target(() => Volatile.Read(ref settled));
        await using var rpc = new JsonRpcConnection(agent.ClientReads,
            new BeforeWrite(agent.ClientWrites, () => Assert.True(Volatile.Read(ref registered) > 0)), target, TranscriptLog.Discard());
        var call = rpc.RequestAsync("probe", JsonSerializer.SerializeToElement(new { }), default,
            onReply: () => Interlocked.Increment(ref replied), onRequest: id => Volatile.Write(ref registered, id), onSettled: () => Interlocked.Increment(ref settled));
        var request = await agent.ReadAsync(); Assert.Equal(registered, request.GetProperty("id").GetInt64());
        var result = error ? "\"error\":{\"code\":-32800,\"message\":\"Cancelled\"}" : "\"result\":{}";
        await agent.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + registered + "," + result + "}\n{\"jsonrpc\":\"2.0\",\"method\":\"after\",\"params\":{}}");
        Assert.Equal(1, await target.Notifications.Reader.ReadAsync().AsTask().WaitAsync(Limit));
        if (error) Assert.Equal(-32800, (await Assert.ThrowsAsync<JsonRpcException>(() => call)).Code);
        else await call.WaitAsync(Limit);
        Assert.Equal(error ? 0 : 1, replied); Assert.Equal(1, settled);
        agent.CloseOutput(); await rpc.Completion.WaitAsync(Limit); Assert.Equal(1, settled);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("write")]
    [InlineData("eof")]
    [InlineData("dispose")]
    public async Task Request_terminal_hook_runs_once_for_every_owned_failure_path(string mode)
    {
        var agent = new FakeAgent(); var settled = 0; var registered = 0L; var replied = 0; var disposed = false;
        var target = new Target(() => Volatile.Read(ref settled));
        var rpc = new JsonRpcConnection(agent.ClientReads, agent.ClientWrites, target, TranscriptLog.Discard());
        using var cancel = new CancellationTokenSource();
        try
        {
            if (mode == "write") agent.BreakInput();
            var call = rpc.RequestAsync("probe", JsonSerializer.SerializeToElement(new { }), cancel.Token,
                onReply: () => Interlocked.Increment(ref replied), onRequest: id => registered = id, onSettled: () => Interlocked.Increment(ref settled));
            if (mode != "write")
            {
                await agent.ReadAsync();
                if (mode == "cancel") cancel.Cancel();
                else if (mode == "eof") agent.CloseOutput();
                else { await rpc.DisposeAsync(); disposed = true; }
            }
            await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(Limit));
            Assert.True(registered > 0); Assert.Equal(1, settled); Assert.Equal(0, replied);
            if (mode == "cancel")
            {
                await agent.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + registered + ",\"result\":{}}\n{\"jsonrpc\":\"2.0\",\"method\":\"after\",\"params\":{}}");
                Assert.Equal(1, await target.Notifications.Reader.ReadAsync().AsTask().WaitAsync(Limit)); Assert.Equal(0, replied);
            }
        }
        finally { agent.CloseOutput(); if (!disposed) await rpc.DisposeAsync(); }
        Assert.Equal(1, settled);
    }

    [Fact]
    public async Task Local_cancellation_clears_url_waiter_sends_one_matching_cancel_and_keeps_fresh_login_usable()
    {
        await using var h = new Harness(); await h.Initialize(); using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = 0;
        var login = h.Client.LoginAsync("agent", _ => { shown++; entered.TrySetResult(); }, (_, _) => new(consent.Task), cancel.Token);
        var request = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(10, LoginScope(request), "https://auth.example.test/cancel")); await entered.Task.WaitAsync(Limit);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(Limit));
        var messages = new[] { await h.Agent.ReadAsync(), await h.Agent.ReadAsync() };
        ActionIs(messages.Single(m => m.TryGetProperty("result", out _)), 10, "cancel");
        var cancellation = messages.Single(m => m.TryGetProperty("method", out _));
        Assert.Equal("$/cancel_request", cancellation.GetProperty("method").GetString());
        Assert.Equal(request.GetProperty("id").GetInt64(), cancellation.GetProperty("params").GetProperty("requestId").GetInt64());
        consent.SetResult(true);
        await h.Agent.ReplyAsync(request, "{}");
        await h.Agent.SendAsync(UrlRequest(11, LoginScope(request), "https://auth.example.test/late")); ActionIs(await h.Agent.ReadAsync(), 11, "cancel");
        Assert.Equal(1, shown); await h.Marker();
        var fresh = h.Client.LoginAsync("agent", _ => { }, default); var next = await h.Agent.ReadAsync();
        await h.Agent.ReplyAsync(next, "{}"); await fresh.WaitAsync(Limit);
    }

    [Fact]
    public async Task Cancellation_inside_url_presentation_never_requests_consent_or_accepts_late_success()
    {
        await using var h = new Harness(); await h.Initialize(); using var cancel = new CancellationTokenSource();
        var consentCalls = 0;
        var login = h.Client.LoginAsync("agent", _ => cancel.Cancel(), (_, _) => { consentCalls++; return ValueTask.FromResult(true); }, cancel.Token);
        var request = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(10, LoginScope(request), "https://auth.example.test/callback-cancel"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(Limit));
        var replies = new[] { await h.Agent.ReadAsync(), await h.Agent.ReadAsync() };
        ActionIs(replies.Single(r => r.TryGetProperty("result", out _)), 10, "cancel");
        var cancellation = replies.Single(r => r.TryGetProperty("method", out _));
        Assert.Equal("$/cancel_request", cancellation.GetProperty("method").GetString());
        Assert.Equal(request.GetProperty("id").GetInt64(), cancellation.GetProperty("params").GetProperty("requestId").GetInt64());
        Assert.Equal(0, consentCalls); await h.Agent.ReplyAsync(request, "{}"); await h.Marker();
    }

    [Fact]
    public async Task Complete_retires_only_its_url_waiter_and_late_true_cannot_accept()
    {
        await using var h = new Harness(); await h.Initialize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var status = 0; h.Client.AuthStatusChanged += _ => status++;
        var login = h.Client.LoginAsync("agent", _ => entered.TrySetResult(), (_, _) => new(consent.Task), default);
        var request = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(10, LoginScope(request), "https://auth.example.test/complete")); await entered.Task.WaitAsync(Limit);
        await h.Agent.SendAsync(Complete("unknown") + "\n" + Complete()); ActionIs(await h.Agent.ReadAsync(), 10, "cancel");
        consent.SetResult(true); await h.Marker(); Assert.False(login.IsCompleted); Assert.Equal(0, status);
        await h.Agent.ReplyAsync(request, "{}"); await login.WaitAsync(Limit); Assert.Equal(0, status);
        await h.Agent.SendAsync(Complete()); await h.Marker();
    }

    [Fact]
    public async Task Completing_one_of_two_pending_urls_leaves_the_other_consent_live()
    {
        await using var h = new Harness(); await h.Initialize();
        var presented = Channel.CreateUnbounded<string>();
        var firstConsent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConsent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = h.Client.LoginAsync("agent", uri => presented.Writer.TryWrite(uri.AbsolutePath),
            (uri, _) => new(uri.AbsolutePath == "/one" ? firstConsent.Task : secondConsent.Task), default);
        var request = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(10, LoginScope(request), "https://auth.example.test/one", "one") + "\n"
            + UrlRequest(11, LoginScope(request), "https://auth.example.test/two", "two"));
        Assert.Equal("/one", await presented.Reader.ReadAsync().AsTask().WaitAsync(Limit));
        Assert.Equal("/two", await presented.Reader.ReadAsync().AsTask().WaitAsync(Limit));
        await h.Agent.SendAsync(Complete("one")); ActionIs(await h.Agent.ReadAsync(), 10, "cancel");
        await h.Marker(); Assert.False(login.IsCompleted);
        secondConsent.SetResult(true); ActionIs(await h.Agent.ReadAsync(), 11, "accept");
        firstConsent.SetResult(true); await h.Agent.ReplyAsync(request, "{}"); await login.WaitAsync(Limit); await h.Marker();
    }

    [Fact]
    public async Task Login_success_retires_pending_consent_before_adjacent_late_url()
    {
        await using var h = new Harness(); await h.Initialize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var shown = 0;
        var login = h.Client.LoginAsync("agent", _ => { shown++; entered.TrySetResult(); }, (_, _) => new(consent.Task), default);
        var request = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(10, LoginScope(request), "https://auth.example.test/first")); await entered.Task.WaitAsync(Limit);
        await h.Agent.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + request.GetProperty("id").GetRawText() + ",\"result\":{}}\n" + UrlRequest(11, LoginScope(request), "https://auth.example.test/late"));
        await login.WaitAsync(Limit);
        var responses = new[] { await h.Agent.ReadAsync(), await h.Agent.ReadAsync() };
        ActionIs(responses.Single(r => r.GetProperty("id").GetInt32() == 10), 10, "cancel");
        ActionIs(responses.Single(r => r.GetProperty("id").GetInt32() == 11), 11, "cancel");
        consent.SetResult(true); await h.Marker(); Assert.Equal(1, shown);
    }

    [Fact]
    public async Task Completed_url_cleanup_cannot_cancel_a_reused_elicitation_id()
    {
        await using var h = new Harness(); await h.Initialize();
        for (var i = 0; i < 32; i++)
        {
            var presented = Channel.CreateUnbounded<string>();
            var oldConsent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var freshConsent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var login = h.Client.LoginAsync("agent", uri => presented.Writer.TryWrite(uri.AbsolutePath),
                (uri, _) => new(uri.AbsolutePath == "/old" ? oldConsent.Task : freshConsent.Task), default);
            var request = await h.Agent.ReadAsync();
            await h.Agent.SendAsync(UrlRequest(10, LoginScope(request), "https://auth.example.test/old", "replace"));
            Assert.Equal("/old", await presented.Reader.ReadAsync().AsTask().WaitAsync(Limit));
            await h.Agent.SendAsync(Complete("replace") + "\n" + UrlRequest(11, LoginScope(request), "https://auth.example.test/fresh", "replace"));
            Assert.Equal("/fresh", await presented.Reader.ReadAsync().AsTask().WaitAsync(Limit));
            ActionIs(await h.Agent.ReadAsync(), 10, "cancel");
            await h.Marker();
            freshConsent.SetResult(true); ActionIs(await h.Agent.ReadAsync(), 11, "accept");
            oldConsent.SetResult(true);
            await h.Agent.ReplyAsync(request, "{}"); await login.WaitAsync(Limit); await h.Marker();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eof_or_disposal_cancels_ignored_consent_and_drops_attempt(bool dispose)
    {
        var h = new Harness(); await h.Initialize();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = h.Client.LoginAsync("agent", _ => { }, (_, ct) => { entered.TrySetResult(ct); return new(consent.Task); }, default);
        var request = await h.Agent.ReadAsync(); await h.Agent.SendAsync(UrlRequest(10, LoginScope(request), "https://auth.example.test/closed"));
        var token = await entered.Task.WaitAsync(Limit);
        if (dispose) await h.DisposeAsync().AsTask().WaitAsync(Limit);
        else h.Agent.CloseOutput();
        await Assert.ThrowsAnyAsync<Exception>(() => login.WaitAsync(Limit));
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => cancelled.TrySetResult()); await cancelled.Task.WaitAsync(Limit);
        consent.SetResult(true);
        if (!dispose) await h.Client.DisposeAsync();
    }

    [Fact]
    public async Task Login_write_failure_removes_correlation_before_later_incoming_url()
    {
        await using var h = new Harness(); await h.Initialize(); h.Agent.BreakInput(); var shown = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => h.Client.LoginAsync("agent", _ => shown++, (_, _) => ValueTask.FromResult(true), default));
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); h.Client.AuthStatusChanged += _ => seen.TrySetResult();
        await h.Agent.SendAsync(UrlRequest(10, "\"requestId\":2", "https://auth.example.test/failed") + "\n{\"jsonrpc\":\"2.0\",\"method\":\"_auth/status_update\",\"params\":{\"authStatus\":{\"kind\":\"marker\"}}}");
        await seen.Task.WaitAsync(Limit); Assert.Equal(0, shown); Assert.False(h.Policy.Urls.Reader.TryRead(out _));
    }

    private sealed class BeforeWrite(Stream inner, Action before) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { before(); return inner.WriteAsync(buffer, ct); }
        public override void Write(byte[] buffer, int offset, int count) { before(); inner.Write(buffer, offset, count); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
