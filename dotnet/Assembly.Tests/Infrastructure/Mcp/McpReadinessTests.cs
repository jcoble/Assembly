using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Hosting;
using Assembly.Engine.Mcp;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Mcp;
using Assembly.Tests.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Mcp;

public sealed class McpReadinessTests
{
    [Fact]
    public async Task Endpoint_installs_actual_address_once_and_stop_fences_late_listening()
    {
        var tokens = new McpTokens();
        var endpoint = new McpEndpointReady(tokens);
        var waiting = endpoint.WaitAsync(default);
        Assert.False(waiting.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => endpoint.ServersFor("s", 4));
        endpoint.Listening(new Uri("http://127.0.0.1:32167"));
        await waiting;
        Assert.Equal("http://127.0.0.1:32167/mcp", Assert.Single(endpoint.ServersFor("s", 4)).GetProperty("url").GetString());
        Assert.Throws<InvalidOperationException>(() => endpoint.Listening(new Uri("http://localhost:1")));
        endpoint.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => endpoint.WaitAsync(default));
        Assert.ThrowsAny<OperationCanceledException>(() => endpoint.ServersFor("s", 4));
        var stopped = new McpEndpointReady(new());
        var pending = stopped.WaitAsync(default);
        stopped.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.ThrowsAny<OperationCanceledException>(() => stopped.Listening(new Uri("http://localhost:1")));
    }

    [Fact]
    public async Task Cancelled_wait_does_not_cancel_endpoint_for_other_openers()
    {
        var endpoint = new McpEndpointReady(new());
        using var cancel = new CancellationTokenSource();
        var waiting = endpoint.WaitAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(endpoint.Ready.IsCompleted);
        endpoint.Listening(new Uri("http://localhost:1"));
        await endpoint.WaitAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_worker_waits_for_listener_and_cancelled_start_leaves_effect_pending(bool cancel)
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession(state: "running");
        Assert.True(f.Apply(new Assembly.Engine.Sessions.StopSession(), "s").Applied);
        var endpoint = new McpEndpointReady(new());
        var effects = new FakeEffects
        {
            Execute = (_, _) => Task.FromResult(new Assembly.Engine.Effects.EffectReceipt(
                Assembly.Engine.Effects.EffectDisposition.Confirmed,
                System.Text.Json.JsonSerializer.SerializeToElement(new Assembly.Engine.Effects.SessionEffectData("idle", null, null)), null))
        };
        var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, effects, effectsReady: endpoint.Ready);
        try
        {
            Assert.False(effects.FirstAttempt.IsCompleted);
            Assert.Equal("pending", f.Text("SELECT state FROM engine_outbox WHERE kind='stop'"));
            if (cancel)
            {
                var stopping = runtime.DisposeAsync().AsTask();
                endpoint.Stop();
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(effects.ExecutedIds);
                Assert.Equal("pending", f.Text("SELECT state FROM engine_outbox WHERE kind='stop'"));
                Assert.Equal(0, f.Number("SELECT attempt FROM engine_outbox WHERE kind='stop'"));
            }
            else
            {
                endpoint.Listening(new Uri("http://localhost:1234"));
                await effects.FirstAttempt.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Single(effects.ExecutedIds);
            }
        }
        finally { await runtime.DisposeAsync(); }
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("idle")]
    [InlineData("running")]
    [InlineData("needs_me")]
    [InlineData("lost")]
    public async Task Opening_uses_original_observation_and_rechecks_it_after_slow_open(string state)
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession(state: state, epoch: 7);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        var tokens = new McpTokens();
        var endpoint = new McpEndpointReady(tokens);
        endpoint.Listening(new Uri("http://localhost:1234"));
        var callbacks = McpSessionCallbacks.Create(runtime, endpoint, tokens);
        var observed = new SessionObservation("s", null, null, null, 7);
        var entry = Assert.Single(callbacks.ServersFor("s", observed));
        var token = entry.GetProperty("headers")[0].GetProperty("value").GetString()![7..];
        Assert.Equal(new McpCaller("s", 7), tokens.Caller(token));
        callbacks.ValidateOpen("s", observed);
        Assert.Equal(new McpCaller("s", 7), tokens.Caller(token));
        if (state == "lost") Assert.Null(((IEngineRuntime)runtime).Read(s => new McpGate(runtime, f.Limits, f.Time).Resolve(s, new("s", 7)), default));
        f.Db.Writer.Write("test.epoch", s => s.Execute("UPDATE engine_session SET epoch=8 WHERE id='s'"));
        Assert.Equal("stale_epoch", Assert.Throws<CallFailed>(() => callbacks.ValidateOpen("s", observed)).Message);
        callbacks.Revoke("s");
        Assert.Null(tokens.Caller(token));
    }

    [Theory]
    [InlineData("ended", "local", "session_gone")]
    [InlineData("foreign", "local", "session_gone")]
    [InlineData("idle", "elsewhere", "session_gone")]
    [InlineData("handing_off", "local", "handing_off")]
    public async Task Invalid_opening_authority_never_issues_token(string state, string holder, string refusal)
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession(state: state, holder: holder);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        var tokens = new McpTokens();
        var endpoint = new McpEndpointReady(tokens);
        endpoint.Listening(new Uri("http://localhost:1234"));
        var callbacks = McpSessionCallbacks.Create(runtime, endpoint, tokens);
        Assert.Equal(refusal, Assert.Throws<CallFailed>(() => callbacks.ServersFor("s", new("s", null, null, null, 0))).Message);
        Assert.Equal("session_gone", Assert.Throws<CallFailed>(() => callbacks.ServersFor("s", null)).Message);
        Assert.Equal("session_gone", Assert.Throws<CallFailed>(() => callbacks.ServersFor("s", new("other", null, null, null, 0))).Message);
    }
}
