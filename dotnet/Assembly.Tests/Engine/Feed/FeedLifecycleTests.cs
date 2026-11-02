using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Engine.Feed;

public sealed class FeedLifecycleTests
{
    [Fact]
    public async Task Actual_provider_state_is_pushed_and_kept_pipe_loss_reports_lost_without_a_self_run()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics);
        var store = SessionStore.Open(f.Db, f.Time, "checkouts");
        var session = store.Create("claude", "https://github.com/o/r");
        var launcher = new FakeLauncher(); var log = new StringWriter();
        using var hub = new LiveHub(RowStore.Open(f.Db));
        await using var work = new BackgroundWork(f.Time, log);
        await using var adapter = new SharedAdapter(launcher, "claude", f.Time, log);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, cutover: new(f.Db, store, launcher, "checkouts", f.Time));
        var events = Channel.CreateUnbounded<EngineEvent>();
        using var subscription = runtime.Publisher.Register(e => events.Writer.TryWrite(e), out _);
        var host = new SessionHost(session.SessionId, hub, store, new(store, launcher, _ => adapter), f.Time, log, work,
            feed: new EngineFeed(runtime, log));
        using var live = hub.Subscribe(session.SessionId, 0);
        try
        {
            host.Send("work"); var agent = await launcher.NextAsync(); await agent.StartAsync();
            var prompt = await agent.ReadAsync("session/prompt");
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"running"}""");
            var running = await Next(e => e.Data.State == "running");
            Assert.Equal(session.SessionId, running.Data.SessionId);
            await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"background","name":"exec_command","status":"in_progress"}""");
            await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
            while ((await live.Rows.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Kind != "stats") { }
            Assert.Null(host.ActiveRunId);
            Assert.True(host.IsOpen);
            agent.Crash();
            var lost = await Next(e => e.Data.State == "lost");
            Assert.Equal(session.SessionId, lost.EntityId);
            Assert.Equal("lost", f.State("engine_session", session.SessionId));
        }
        finally { await host.StopForShutdownAsync(); }

        async Task<EngineEvent> Next(Func<EngineEvent, bool> matches)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var e = await events.Reader.ReadAsync(deadline.Token);
                if (matches(e)) return e;
            }
        }
    }

    [Theory]
    [InlineData("starting", "starting")]
    [InlineData("lost", "idle")]
    public async Task Connected_reports_real_capabilities_and_reconnects_only_lost(string initial, string expected)
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics);
        var store = SessionStore.Open(f.Db, f.Time, "checkouts");
        var session = store.Create("claude", "https://github.com/o/r");
        var launcher = new FakeLauncher(); var log = new StringWriter();
        using var hub = new LiveHub(RowStore.Open(f.Db));
        await using var work = new BackgroundWork(f.Time, log);
        await using var adapter = new SharedAdapter(launcher, "claude", f.Time, log);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, cutover: new(f.Db, store, launcher, "checkouts", f.Time));
        f.Db.Writer.Write("test.initial", s => s.Execute("UPDATE engine_session SET state=$state", new Assembly.Application.Common.Database.SqlArg("$state", initial)));
        var host = new SessionHost(session.SessionId, hub, store, new(store, launcher, _ => adapter), f.Time, log, work,
            feed: new EngineFeed(runtime, log));
        try
        {
            host.Send("hi"); var agent = await launcher.NextAsync(); await agent.StartAsync(canSteer: true);
            var prompt = await agent.ReadAsync("session/prompt");
            Assert.Equal(expected, f.State("engine_session", session.SessionId));
            Assert.Equal(1, f.Number("SELECT can_steer FROM engine_session"));
            await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
        }
        finally { await host.StopForShutdownAsync(); }
    }
}
