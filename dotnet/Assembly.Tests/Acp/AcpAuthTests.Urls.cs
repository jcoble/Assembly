using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Tests.Acp;

public sealed partial class AcpAuthTests
{
    private static string UrlRequest(int id, string scope, string url, string? elicitationId = "e") =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"elicitation/create\",\"params\":{" + scope +
        ",\"mode\":\"url\",\"url\":" + JsonSerializer.Serialize(url) + ",\"message\":" + JsonSerializer.Serialize(url) +
        ",\"repeated\":{" + "\"secret\":" + JsonSerializer.Serialize(url) + "},\"elicitationId\":" + JsonSerializer.Serialize(elicitationId) + "}}";
    private static string LoginScope(JsonElement request) => "\"requestId\":" + request.GetProperty("id").GetRawText();
    private static string Complete(string id = "e") => "{\"jsonrpc\":\"2.0\",\"method\":\"elicitation/complete\",\"params\":{\"elicitationId\":" + JsonSerializer.Serialize(id) + "}}";
    private static void ActionIs(JsonElement response, int id, string action)
    {
        Assert.Equal(id, response.GetProperty("id").GetInt32());
        var result = response.GetProperty("result");
        Assert.Equal(action, result.GetProperty("action").GetString());
        Assert.False(result.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task Full_login_url_waits_for_explicit_consent_and_complete_is_not_login_success()
    {
        var transcript = new StringWriter(); await using var h = new Harness(new TranscriptLog(transcript)); await h.Initialize();
        var full = "https://auth.example.test/a%2Fb?canary=URL_LOGIN_SECRET&escaped=%2F%3F&large=" + new string('x', 40000) + "#original%20fragment";
        var presented = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = h.Client.LoginAsync("agent", uri => presented.TrySetResult(uri), (uri, ct) =>
        {
            Assert.Equal(full, uri.OriginalString);
            return new(consent.Task.WaitAsync(ct));
        }, default);
        var request = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(10, LoginScope(request), full));
        Assert.Equal(full, (await presented.Task.WaitAsync(Limit)).OriginalString);
        await h.Marker(); // no response can precede this marker while consent is held
        Assert.False(login.IsCompleted); Assert.False(h.Policy.Urls.Reader.TryRead(out _)); Assert.False(h.Client.Updates.TryRead(out _));
        consent.SetResult(true); ActionIs(await h.Agent.ReadAsync(), 10, "accept");
        await h.Agent.SendAsync(Complete()); await h.Marker(); Assert.False(login.IsCompleted);
        await h.Agent.ReplyErrorAsync(request, -32000, "Still requires sign in");
        Assert.True((await Assert.ThrowsAsync<JsonRpcException>(() => login)).Error.IsAuthRequired);
        Assert.DoesNotContain("URL_LOGIN_SECRET", transcript.ToString());
        Assert.Contains("[redacted", transcript.ToString());
    }

    [Fact]
    public async Task Url_payload_is_redacted_even_without_a_matching_login()
    {
        var transcript = new StringWriter(); await using var h = new Harness(new TranscriptLog(transcript));
        await h.Agent.SendAsync(UrlRequest(10, "\"requestId\":999", "https://auth.example.test/?secret=REDACT_SECRET"));
        ActionIs(await h.Agent.ReadAsync(), 10, "cancel");
        Assert.DoesNotContain("REDACT_SECRET", transcript.ToString());
    }

    [Fact]
    public async Task Legacy_login_presents_but_never_assumes_consent()
    {
        await using var h = new Harness(); await h.Initialize();
        var presented = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = h.Client.LoginAsync("agent", uri => presented.TrySetResult(uri), default);
        var request = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(10, LoginScope(request), "https://auth.example.test/legacy"));
        await presented.Task.WaitAsync(Limit); ActionIs(await h.Agent.ReadAsync(), 10, "cancel");
        Assert.False(login.IsCompleted);
        await h.Agent.ReplyAsync(request, "{}"); await login.WaitAsync(Limit);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("callback-failure")]
    [InlineData("consent-failure")]
    public async Task Denied_or_throwing_url_callbacks_cancel_without_leaking_url(string mode)
    {
        var transcript = new StringWriter(); await using var h = new Harness(new TranscriptLog(transcript)); await h.Initialize();
        const string url = "https://auth.example.test/?canary=URL_FAILURE_SECRET";
        var login = h.Client.LoginAsync("agent", _ => { if (mode == "callback-failure") throw new InvalidOperationException(url); },
            (_, _) => mode == "consent-failure" ? throw new InvalidOperationException(url) : ValueTask.FromResult(false), default);
        var request = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(10, LoginScope(request), url));
        var response = await h.Agent.ReadAsync(); ActionIs(response, 10, "cancel");
        Assert.DoesNotContain("URL_FAILURE_SECRET", response.GetRawText());
        await h.Agent.ReplyAsync(request, "{}"); await login.WaitAsync(Limit);
        Assert.DoesNotContain("URL_FAILURE_SECRET", transcript.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reply_immediately_followed_by_late_url_is_fenced_before_reader_continues(bool error)
    {
        await using var h = new Harness(); await h.Initialize(); var shown = 0;
        var login = h.Client.LoginAsync("agent", _ => Interlocked.Increment(ref shown), (_, _) => ValueTask.FromResult(true), default);
        var request = await h.Agent.ReadAsync(); var id = request.GetProperty("id").GetRawText();
        var reply = error ? "\"error\":{\"code\":-32800,\"message\":\"Cancelled\"}" : "\"result\":{}";
        await h.Agent.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + "," + reply + "}\n" + UrlRequest(10, LoginScope(request), "https://auth.example.test/late"));
        ActionIs(await h.Agent.ReadAsync(), 10, "cancel");
        if (error) Assert.Equal(-32800, (await Assert.ThrowsAsync<JsonRpcException>(() => login)).Code);
        else await login.WaitAsync(Limit);
        Assert.Equal(0, shown); Assert.False(h.Policy.Urls.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("string")]
    [InlineData("wrong")]
    public async Task String_or_wrong_outgoing_request_identity_cannot_reach_login_callback(string mode)
    {
        await using var h = new Harness(); await h.Initialize(); var shown = 0;
        var login = h.Client.LoginAsync("agent", _ => shown++, (_, _) => ValueTask.FromResult(true), default);
        var request = await h.Agent.ReadAsync(); var id = request.GetProperty("id").GetInt64();
        var scope = "\"requestId\":" + (mode == "string" ? JsonSerializer.Serialize(id.ToString(System.Globalization.CultureInfo.InvariantCulture)) : (id + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await h.Agent.SendAsync(UrlRequest(10, scope, "https://auth.example.test/wrong"));
        ActionIs(await h.Agent.ReadAsync(), 10, "cancel"); Assert.Equal(0, shown); Assert.False(h.Policy.Urls.Reader.TryRead(out _));
        await h.Agent.ReplyAsync(request, "{}"); await login.WaitAsync(Limit);
    }

    [Fact]
    public async Task Concurrent_reverse_logins_and_overlapping_connection_ids_stay_with_their_owner()
    {
        await using var a = new Harness(); await using var b = new Harness(); await a.Initialize(); await b.Initialize();
        var shown = Channel.CreateUnbounded<string>();
        var first = a.Client.LoginAsync("agent", uri => shown.Writer.TryWrite("first:" + uri.AbsolutePath), (_, _) => ValueTask.FromResult(true), default);
        var r1 = await a.Agent.ReadAsync();
        var second = a.Client.LoginAsync("agent", uri => shown.Writer.TryWrite("second:" + uri.AbsolutePath), (_, _) => ValueTask.FromResult(true), default);
        var r2 = await a.Agent.ReadAsync();
        // B has no corresponding login yet: A's outgoing ID has no authority on B.
        await b.Agent.SendAsync(UrlRequest(9, LoginScope(r1), "https://auth.example.test/foreign"));
        ActionIs(await b.Agent.ReadAsync(), 9, "cancel");
        var other = b.Client.LoginAsync("agent", uri => shown.Writer.TryWrite("other:" + uri.AbsolutePath), (_, _) => ValueTask.FromResult(true), default);
        var rb = await b.Agent.ReadAsync(); Assert.Equal(r1.GetProperty("id").GetInt64(), rb.GetProperty("id").GetInt64());
        await a.Agent.SendAsync(UrlRequest(11, LoginScope(r2), "https://auth.example.test/two", "two") + "\n" + UrlRequest(10, LoginScope(r1), "https://auth.example.test/one", "one"));
        await b.Agent.SendAsync(UrlRequest(10, LoginScope(rb), "https://auth.example.test/other", "one"));
        var values = new[] { await shown.Reader.ReadAsync(), await shown.Reader.ReadAsync(), await shown.Reader.ReadAsync() };
        Assert.Equal(["first:/one", "other:/other", "second:/two"], values.Order(StringComparer.Ordinal));
        ActionIs(await a.Agent.ReadAsync(), 11, "accept"); ActionIs(await a.Agent.ReadAsync(), 10, "accept"); ActionIs(await b.Agent.ReadAsync(), 10, "accept");
        await a.Agent.ReplyAsync(r2, "{}"); await a.Agent.ReplyAsync(r1, "{}"); await b.Agent.ReplyAsync(rb, "{}");
        await Task.WhenAll(first, second, other).WaitAsync(Limit);
        Assert.False(a.Client.Updates.TryRead(out _)); Assert.False(b.Client.Updates.TryRead(out _));
    }

    [Fact]
    public async Task Duplicate_outstanding_elicitation_id_has_no_second_consent_waiter()
    {
        await using var h = new Harness(); await h.Initialize(); var shown = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = h.Client.LoginAsync("agent", _ => { shown++; entered.TrySetResult(); }, (_, ct) => new(consent.Task.WaitAsync(ct)), default);
        var request = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(10, LoginScope(request), "https://auth.example.test/first")); await entered.Task.WaitAsync(Limit);
        await h.Agent.SendAsync(UrlRequest(11, LoginScope(request), "https://auth.example.test/duplicate"));
        ActionIs(await h.Agent.ReadAsync(), 11, "cancel"); Assert.Equal(1, shown);
        consent.SetResult(true); ActionIs(await h.Agent.ReadAsync(), 10, "accept");
        await h.Agent.ReplyAsync(request, "{}"); await login.WaitAsync(Limit);
    }

    [Theory]
    [InlineData("\"sessionId\":\"s\",\"requestId\":2", "https://auth.example.test/ok", "e")]
    [InlineData("\"other\":1", "https://auth.example.test/ok", "e")]
    [InlineData("\"requestId\":2", "not an absolute URL SECRET_INVALID_URL", "e")]
    [InlineData("\"requestId\":2", "https://auth.example.test/SECRET_INVALID_URL", null)]
    public async Task Invalid_url_scope_or_fields_return_fixed_invalid_params(string scope, string url, string? elicitation)
    {
        var transcript = new StringWriter(); await using var h = new Harness(new TranscriptLog(transcript));
        await h.Agent.SendAsync(UrlRequest(10, scope, url, elicitation));
        var response = await h.Agent.ReadAsync(); Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain("SECRET_INVALID_URL", response.GetRawText()); Assert.DoesNotContain("SECRET_INVALID_URL", transcript.ToString());
        Assert.False(h.Policy.Urls.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Session_full_url_uses_constructor_policy_host_and_privacy_boundary()
    {
        var transcript = new StringWriter(); await using var h = new Harness(new TranscriptLog(transcript));
        var full = "https://session.example.test/a%2Fb?canary=SESSION_URL_SECRET&large=" + new string('x', 40000) + "#preserved%20fragment";
        var run = h.Client.PromptAsync("s", [TextBlock.Of("hi")], default); var prompt = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(10, "\"sessionId\":\"s\"", full));
        ActionIs(await h.Agent.ReadAsync(), 10, "accept");
        var asked = await h.Policy.Urls.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(("s", "session.example.test", full), (asked.Session, asked.Host, asked.Url.OriginalString));
        var decided = await h.Policy.Decisions.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        Assert.Equal("accept", decided.Outcome.GetProperty("action").GetString());
        Assert.DoesNotContain("SESSION_URL_SECRET", transcript.ToString());
        await Finish(h.Agent, prompt); await run.Ended.WaitAsync(Limit); await run.Inserted.WaitAsync(Limit);
    }

    [Fact]
    public async Task Native_session_url_keeps_actual_child_and_direct_policy_precedence()
    {
        await using var h = new Harness(); var parent = new Policy(); var child = new Policy(); h.Client.SetSessionPolicy("s", parent);
        var run = h.Client.PromptAsync("s", [TextBlock.Of("hi")], default); var prompt = await h.Agent.ReadAsync();
        await h.Agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"subagent_update","sessionId":"child","title":"Child","state":{"state":"running"}}}}""" + "\n" + UrlRequest(10, "\"sessionId\":\"child\"", "https://auth.example.test/native"));
        Assert.Equal("child", (await parent.Urls.Reader.ReadAsync().AsTask().WaitAsync(Limit)).Session); ActionIs(await h.Agent.ReadAsync(), 10, "accept");
        h.Client.SetSessionPolicy("child", child);
        var childRun = h.Client.PromptAsync("child", [TextBlock.Of("own")], default); var childPrompt = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(11, "\"sessionId\":\"child\"", "https://auth.example.test/direct", "direct"));
        Assert.Equal("child", (await child.Urls.Reader.ReadAsync().AsTask().WaitAsync(Limit)).Session); ActionIs(await h.Agent.ReadAsync(), 11, "accept");
        Assert.False(h.Policy.Urls.Reader.TryRead(out _)); Assert.False(parent.Urls.Reader.TryRead(out _));
        await Finish(h.Agent, childPrompt, "child"); await childRun.Ended.WaitAsync(Limit); await childRun.Inserted.WaitAsync(Limit);
        await Finish(h.Agent, prompt); await run.Ended.WaitAsync(Limit); await run.Inserted.WaitAsync(Limit);
    }

    private sealed class DefaultUrlPolicy : IPermissionPolicy
    {
        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken ct) => Task.FromResult(PermissionOutcome.Cancelled);
    }

    [Fact]
    public async Task Session_url_preserves_idle_guard_and_default_policy_denial()
    {
        await using var h = new Harness();
        await h.Agent.SendAsync(UrlRequest(10, "\"sessionId\":\"s\"", "https://auth.example.test/idle"));
        ActionIs(await h.Agent.ReadAsync(), 10, "cancel"); Assert.False(h.Policy.Urls.Reader.TryRead(out _));
        h.Client.SetSessionPolicy("s", new DefaultUrlPolicy());
        var run = h.Client.PromptAsync("s", [TextBlock.Of("hi")], default); var prompt = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(11, "\"sessionId\":\"s\"", "https://auth.example.test/default"));
        ActionIs(await h.Agent.ReadAsync(), 11, "cancel"); Assert.False(h.Policy.Urls.Reader.TryRead(out _));
        await Finish(h.Agent, prompt); await run.Ended.WaitAsync(Limit); await run.Inserted.WaitAsync(Limit);
    }

    [Fact]
    public async Task Session_foreground_end_cancels_policy_that_ignores_token_and_late_true_is_inert()
    {
        await using var h = new Harness();
        var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Policy.Consent = _ => new(consent.Task);
        var run = h.Client.PromptAsync("s", [TextBlock.Of("hi")], default); var prompt = await h.Agent.ReadAsync();
        await h.Agent.SendAsync(UrlRequest(10, "\"sessionId\":\"s\"", "https://auth.example.test/pending"));
        var asked = await h.Policy.Urls.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        await Finish(h.Agent, prompt); await run.Ended.WaitAsync(Limit); await run.Inserted.WaitAsync(Limit);
        ActionIs(await h.Agent.ReadAsync(), 10, "cancel"); Assert.True(asked.Token.IsCancellationRequested);
        consent.SetResult(true); await h.Marker(); Assert.False(h.Policy.Decisions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Form_mode_keeps_schema_incoming_context_decided_and_unredacted_transcript()
    {
        var transcript = new StringWriter(); await using var h = new Harness(new TranscriptLog(transcript));
        var run = h.Client.PromptAsync("s", [TextBlock.Of("hi")], default); var prompt = await h.Agent.ReadAsync();
        await h.Agent.SendAsync("""{"jsonrpc":"2.0","id":"form-id","method":"elicitation/create","params":{"sessionId":"s","mode":"form","message":"FORM_VISIBLE","requestedSchema":{"type":"object","properties":{"answer":{"type":"string"}}}}}""");
        var asked = await h.Policy.Forms.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        Assert.Equal("form-id", asked.Context.Id.GetString()); Assert.Equal("string", asked.Request.RequestedSchema.GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());
        var response = await h.Agent.ReadAsync(); Assert.Equal("original", response.GetProperty("result").GetProperty("content").GetProperty("answer").GetString());
        Assert.Equal("s", (await h.Policy.Decisions.Reader.ReadAsync().AsTask().WaitAsync(Limit)).Session);
        Assert.Contains("FORM_VISIBLE", transcript.ToString()); Assert.False(h.Policy.Urls.Reader.TryRead(out _));
        await Finish(h.Agent, prompt); await run.Ended.WaitAsync(Limit); await run.Inserted.WaitAsync(Limit);
    }
}
