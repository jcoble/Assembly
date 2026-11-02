using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Tests.Core.Acp;

namespace Assembly.Tests.Acp;

public sealed partial class AcpAuthTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private const string Methods = """[{"methodId":"agent","name":"Agent","type":"agent"},{"methodId":"terminal","name":"Terminal","type":"terminal"},{"methodId":"custom","name":"Custom","type":"_custom"}]""";

    private sealed class Policy : IPermissionPolicy
    {
        public readonly Channel<(string? Session, Uri Url, string Host, CancellationToken Token)> Urls = Channel.CreateUnbounded<(string?, Uri, string, CancellationToken)>();
        public readonly Channel<(ElicitationRequest Request, JsonRpcRequestContext Context)> Forms = Channel.CreateUnbounded<(ElicitationRequest, JsonRpcRequestContext)>();
        public readonly Channel<(string Session, JsonElement Outcome)> Decisions = Channel.CreateUnbounded<(string, JsonElement)>();
        public Func<CancellationToken, ValueTask<bool>> Consent { get; set; } = _ => ValueTask.FromResult(true);
        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken ct) => Task.FromResult(PermissionOutcome.Cancelled);
        public ValueTask<bool> ElicitUrlAsync(string? sessionId, Uri url, string host, CancellationToken ct)
        {
            Urls.Writer.TryWrite((sessionId, url, host, ct));
            return Consent(ct);
        }
        public Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, JsonRpcRequestContext context, CancellationToken ct)
        {
            Forms.Writer.TryWrite((request, context));
            return Task.FromResult(new ElicitationResponse("accept", JsonSerializer.SerializeToElement(new { answer = "original" })));
        }
        public void Decided(string method, string session, JsonRpcRequestContext context, JsonElement outcome) => Decisions.Writer.TryWrite((session, outcome));
    }

    private sealed class Harness : IAsyncDisposable
    {
        public FakeAgent Agent { get; } = new();
        public Policy Policy { get; } = new();
        public AcpClient Client { get; }
        public Harness(TranscriptLog? transcript = null) => Client = new(Agent.ClientReads, Agent.ClientWrites, Policy, transcript ?? TranscriptLog.Discard());
        public async Task<InitializeResponse> Initialize(string? methods = Methods)
        {
            var call = Client.InitializeAsync(new("fixture", "1"), true, default);
            var request = await Agent.ReadAsync();
            var capabilities = request.GetProperty("params").GetProperty("capabilities");
            Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("auth").GetProperty("terminal").ValueKind);
            Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("elicitation").GetProperty("form").ValueKind);
            Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("elicitation").GetProperty("url").ValueKind);
            var field = methods is null ? "" : ",\"authMethods\":" + methods;
            await Agent.ReplyAsync(request, "{\"protocolVersion\":2,\"capabilities\":{\"session\":{\"delete\":{}}},\"_meta\":{\"steering\":{\"supported\":true}}" + field + "}");
            return await call.WaitAsync(Limit);
        }
        public async Task Marker()
        {
            var call = Client.NewSessionAsync(new("/fixture", []), default);
            var marker = await Agent.ReadAsync();
            Assert.Equal("session/new", marker.GetProperty("method").GetString());
            await Agent.ReplyAsync(marker, """{"sessionId":"marker"}""");
            await call.WaitAsync(Limit);
        }
        public async ValueTask DisposeAsync()
        {
            Agent.CloseOutput();
            await Client.DisposeAsync();
        }
    }

    [Fact, Trait("AuthPhase", "Calls")]
    public async Task Auth_methods_parse_method_id_type_env_list_and_cloned_meta()
    {
        await using var h = new Harness();
        var initialized = await h.Initialize("""[{"methodId":"api-key","name":"API key","type":"agent","_meta":{"nested":{"mode":"key"}}},{"methodId":"gateway","name":"Gateway","type":"agent","_meta":{"nested":{"mode":"gateway"}}},{"methodId":"cli","name":"CLI","type":"terminal","args":["login","--flag"],"env":[{"name":"FIRST","value":"1"},{"name":"SECOND","value":"2"}]},{"methodId":"extension","name":"Extension","type":"_custom","_meta":{"keep":[1,2]}}]""");
        Assert.Equal(["api-key", "gateway", "cli", "extension"], initialized.AuthMethods.Select(m => m.MethodId));
        Assert.Equal("key", initialized.AuthMethods[0].Meta!.Value.GetProperty("nested").GetProperty("mode").GetString());
        Assert.Equal("gateway", initialized.AuthMethods[1].Meta!.Value.GetProperty("nested").GetProperty("mode").GetString());
        Assert.Equal(["login", "--flag"], initialized.AuthMethods[2].Args);
        Assert.Equal([new AuthEnv("FIRST", "1"), new AuthEnv("SECOND", "2")], initialized.AuthMethods[2].Env);
        Assert.Equal("_custom", initialized.AuthMethods[3].Type);
        Assert.Equal(2, initialized.AuthMethods[3].Meta!.Value.GetProperty("keep")[1].GetInt32());
        Assert.True(initialized.CanSteer); Assert.True(initialized.CanDelete);
    }

    [Theory, Trait("AuthPhase", "Calls")]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("[{\"methodId\":\"unknown\",\"name\":\"Future\",\"type\":\"future\"}]")]
    public async Task Missing_null_or_non_array_auth_methods_normalize_to_empty(string? methods)
    {
        await using var h = new Harness();
        Assert.Empty((await h.Initialize(methods)).AuthMethods);
        await h.Client.LogoutAsync(default);
        await h.Marker();
    }

    [Fact, Trait("AuthPhase", "Calls")]
    public async Task Malformed_descriptors_are_skipped_without_discarding_valid_neighbors()
    {
        await using var h = new Harness();
        var response = await h.Initialize("""[{"methodId":"first","name":"First","type":"agent"},{},null,3,{"name":"Missing","type":"agent"},{"methodId":1,"name":"Bad","type":"agent"},{"methodId":"bad","name":3,"type":"agent"},{"methodId":"bad","name":"Bad","type":3},{"methodId":"future","name":"Future","type":"future"},{"methodId":"bad","name":"Bad","type":"terminal","args":[1]},{"methodId":"bad","name":"Bad","type":"terminal","env":[{"name":"X","value":3}]},{"methodId":"last","name":"Last","type":"_extension","_meta":{"x":true}}]""");
        Assert.Equal(["first", "last"], response.AuthMethods.Select(m => m.MethodId));
    }

    [Fact, Trait("AuthPhase", "Calls")]
    public async Task Login_and_logout_send_exact_v2_methods_and_wait_for_their_responses()
    {
        await using var h = new Harness(); await h.Initialize();
        var login = h.Client.LoginAsync("agent", _ => throw new InvalidOperationException("No URL expected"), default);
        var request = await h.Agent.ReadAsync();
        Assert.Equal("auth/login", request.GetProperty("method").GetString());
        Assert.Equal("{\"methodId\":\"agent\"}", request.GetProperty("params").GetRawText());
        Assert.False(login.IsCompleted);
        await h.Agent.ReplyAsync(request, "{}"); await login.WaitAsync(Limit);
        var logout = h.Client.LogoutAsync(default); var end = await h.Agent.ReadAsync();
        Assert.Equal("auth/logout", end.GetProperty("method").GetString());
        Assert.Equal("{}", end.GetProperty("params").GetRawText());
        await h.Agent.ReplyAsync(end, "{}"); await logout.WaitAsync(Limit);
    }

    [Theory, Trait("AuthPhase", "Calls")]
    [InlineData("terminal")]
    [InlineData("custom")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task Terminal_custom_unknown_and_empty_methods_never_send_login(string method)
    {
        await using var h = new Harness(); await h.Initialize();
        var error = await Assert.ThrowsAsync<JsonRpcException>(() => h.Client.LoginAsync(method, _ => { }, default));
        Assert.Equal(JsonRpcException.InvalidParams, error.Code);
        await h.Marker();
    }

    [Fact, Trait("AuthPhase", "Calls")]
    public async Task Nonempty_terminal_only_methods_still_enable_logout()
    {
        await using var h = new Harness(); await h.Initialize("""[{"methodId":"cli","name":"CLI","type":"terminal"}]""");
        var call = h.Client.LogoutAsync(default); var request = await h.Agent.ReadAsync();
        Assert.Equal("auth/logout", request.GetProperty("method").GetString());
        await h.Agent.ReplyAsync(request, "{}"); await call.WaitAsync(Limit);
    }

    [Fact, Trait("AuthPhase", "Calls")]
    public async Task Auth_status_is_a_connection_event_and_malformed_status_does_not_break_prompt()
    {
        await using var h = new Harness();
        var events = Channel.CreateUnbounded<AuthStatus>();
        h.Client.AuthStatusChanged += status => events.Writer.TryWrite(status);
        await h.Agent.SendAsync("""{"jsonrpc":"2.0","method":"_auth/status_update","params":{"authStatus":{"kind":"future-kind","label":"Account"}}}""");
        Assert.Equal(new AuthStatus("future-kind", "Account"), await events.Reader.ReadAsync().AsTask().WaitAsync(Limit));
        Assert.False(h.Client.Updates.TryRead(out _));
        foreach (var invalid in new[] { "{}", "null", "{\"authStatus\":{}}", "{\"authStatus\":{\"kind\":3}}" })
            await h.Agent.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"_auth/status_update\",\"params\":" + invalid + "}");
        var run = h.Client.PromptAsync("s", [TextBlock.Of("once")], default);
        var prompt = await h.Agent.ReadAsync(); await Finish(h.Agent, prompt);
        Assert.Equal("m", await run.Inserted.WaitAsync(Limit));
        Assert.Equal("end_turn", (await run.Ended.WaitAsync(Limit)).StopReason);
        Assert.False(events.Reader.TryRead(out _));
    }

    [Fact, Trait("AuthPhase", "Calls")]
    public async Task Auth_required_is_preserved_before_and_after_one_insertion_without_retry()
    {
        await using var h = new Harness();
        var opening = h.Client.NewSessionAsync(new("/fixture", []), default); var request = await h.Agent.ReadAsync();
        await h.Agent.ReplyErrorAsync(request, -32000, "Sign in");
        Assert.True((await Assert.ThrowsAsync<JsonRpcException>(() => opening)).Error.IsAuthRequired);
        var rejected = h.Client.PromptAsync("s", [TextBlock.Of("once")], default); request = await h.Agent.ReadAsync();
        await h.Agent.ReplyErrorAsync(request, -32000, "Sign in");
        Assert.True((await Assert.ThrowsAsync<JsonRpcException>(() => rejected.Inserted)).Error.IsAuthRequired);
        await Assert.ThrowsAsync<JsonRpcException>(() => rejected.Ended);
        var run = h.Client.PromptAsync("s", [TextBlock.Of("again")], default); request = await h.Agent.ReadAsync();
        await h.Agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"user_message","messageId":"m"}}}""");
        await h.Agent.ReplyAsync(request, """{"messageId":"m"}""");
        Assert.Equal("m", await run.Inserted.WaitAsync(Limit));
        await h.Agent.SendAsync("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"error","error":{"code":-32000,"message":"Sign in"}}}}""");
        Assert.True((await run.Ended.WaitAsync(Limit)).Error!.IsAuthRequired);
        await h.Marker();
    }

    private static async Task Finish(FakeAgent agent, JsonElement prompt, string session = "s")
    {
        await agent.SendAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = session, update = new { sessionUpdate = "user_message", messageId = "m" } } }));
        await agent.ReplyAsync(prompt, """{"messageId":"m"}""");
        await agent.SendAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = session, update = new { sessionUpdate = "state_update", state = "running" } } }));
        await agent.SendAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = session, update = new { sessionUpdate = "state_update", state = "idle", stopReason = "end_turn" } } }));
    }
}
