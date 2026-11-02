using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Hosting;
using Assembly.Engine.Mcp;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Mcp;
using Assembly.Infrastructure.Topics;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task Actual_registry_lost_reopen_keeps_original_epoch_and_recovers_only_after_provider_response()
    {
        var tokens = new McpTokens();
        var endpoint = new McpEndpointReady(tokens);
        endpoint.Listening(new Uri("http://localhost:12345"));
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time), effectsReady: endpoint.Ready);
        var callbacks = McpSessionCallbacks.Create(runtime, endpoint, tokens);
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work,
            feed: new EngineFeed(runtime, _log, _time), mcp: callbacks);
        var host = registry.For(_session.SessionId);
        Assert.NotNull(host.Send("first"));
        var agent = await _launcher.NextAsync();
        var first = await agent.StartAsync();
        var oldToken = Token(first);
        await agent.ReadAsync("session/prompt");
        agent.Crash();
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "failed");
        Assert.Equal("lost", State());
        Assert.NotNull(host.Send("again"));
        var second = await _launcher.NextAsync();
        var initialize = await second.ReadAsync("initialize");
        await second.ReplyAsync(initialize, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
        var resume = await second.ReadAsync("session/resume");
        Assert.Equal(FakeLink.AcpSessionId, resume.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Null(tokens.Caller(oldToken));
        var token = Token(resume);
        var caller = Assert.IsType<McpCaller>(tokens.Caller(token));
        Assert.Equal(_session.SessionId, caller.SessionId);
        Assert.Equal("lost", State());
        var gate = new McpGate(runtime, new(() => 1, () => 5), _time);
        Assert.Null(((IEngineRuntime)runtime).Read(scope => gate.Resolve(scope, caller), default));
        await second.ReplyAsync(resume, "{}");
        var prompt = await second.ReadAsync("session/prompt");
        Assert.Equal("idle", State());
        Assert.NotNull(((IEngineRuntime)runtime).Read(scope => gate.Resolve(scope, caller), default));
        Assert.Equal("again", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal(2, _launcher.Launches);
        Assert.True(agent.Disposed);
        await second.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await second.CloseAsync();
        Assert.Null(tokens.Caller(token));

        string? State() => _database.QueryFirst("SELECT state FROM engine_session WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", _session.SessionId));
        static string Token(System.Text.Json.JsonElement open)
        {
            var server = open.GetProperty("params").GetProperty("mcpServers")[0];
            Assert.Equal("http://localhost:12345/mcp", server.GetProperty("url").GetString());
            return server.GetProperty("headers")[0].GetProperty("value").GetString()![7..];
        }
    }
}
