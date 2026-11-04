using Assembly.Application.Features.Chat.Rows;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Topics;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    private sealed class MeasuringFeed(ISessionFeed inner) : ISessionFeed
    {
        public readonly Channel<SessionTurnFinalization> Finals = Channel.CreateUnbounded<SessionTurnFinalization>();
        public readonly Channel<(SessionObservation Observed, string Run)> Releases = Channel.CreateUnbounded<(SessionObservation, string)>();
        public SessionObservation? Capture(string id) => inner.Capture(id);
        public void ObserveBatch(IWriteScope s, SessionObservation o, string acp, IReadOnlyList<AgentEvent> events) => inner.ObserveBatch(s, o, acp, events);
        public Action<IWriteScope> PrepareAsked(SessionObservation o, string id, AskPayload card, JsonRpcRequestContext request) => inner.PrepareAsked(o, id, card, request);
        public Action<IWriteScope> PrepareSettled(SessionObservation o, string id, string how, JsonElement? answer, string reason) => inner.PrepareSettled(o, id, how, answer, reason);
        public Action<IWriteScope> PreparePolicyAnswered(SessionObservation o, JsonRpcRequestContext request, string method, JsonElement outcome) => inner.PreparePolicyAnswered(o, request, method, outcome);
        public void Connected(IWriteScope s, SessionObservation o, string acp, bool canSteer) => inner.Connected(s, o, acp, canSteer);
        public void Lost(IWriteScope s, SessionObservation o, string reason) => inner.Lost(s, o, reason);
        public void BlockSignInNeeded(IWriteScope s, SessionObservation o, string acp, AuthTerminalProof p) => inner.BlockSignInNeeded(s, o, acp, p);
        public void SignInNeeded(IWriteScope s, SessionObservation o, string acp, long seq, AuthTerminalProof p) => inner.SignInNeeded(s, o, acp, seq, p);
        public void BlockFailedOpenSignInNeeded(IWriteScope s, SessionObservation o, AuthTerminalProof p) => inner.BlockFailedOpenSignInNeeded(s, o, p);
        public void FailedOpenSignInNeeded(IWriteScope s, SessionObservation o, long seq, AuthTerminalProof p) => inner.FailedOpenSignInNeeded(s, o, seq, p);
        public void RetryStarted(IWriteScope s, SessionObservation o, AuthRetryExecution e) => inner.RetryStarted(s, o, e);
        public void Replied(IWriteScope s, SessionObservation o, SessionTurnReply final) => inner.Replied(s, o, final);
        public void Recovered(IWriteScope s, RecoveredChatTurn recovered) => inner.Recovered(s, recovered);
        public void Released(SessionObservation observed, string chatRunId)
        {
            inner.Released(observed, chatRunId);
            Releases.Writer.TryWrite((observed, chatRunId));
        }
        public bool Complete(IWriteScope s, SessionObservation o, string acp, SessionTurnFinalization final)
        {
            var applied = inner.Complete(s, o, acp, final);
            Finals.Writer.TryWrite(final);
            return applied;
        }
    }

    [Fact]
    public async Task Self_started_terminal_uses_later_cost_in_same_reader_list_but_never_invents_a_prompt_id()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, _db.Folder, _time));
        var feed = new MeasuringFeed(new EngineFeed(runtime, _log, _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: feed);
        var host = registry.For(_session.SessionId); host.Send("work");
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"background","name":"exec_command","status":"in_progress"}""");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn","usage":{"costUsd":1}}""");
        await RowsUntil(row => row.Kind == "stats");
        var first = await feed.Finals.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.NotNull(first.ProviderMessageId); Assert.Equal(1m, first.CostUsd);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"running"}""");
        await agent.TextAsync("continuing"); await RowsUntil(row => row.Kind == "delta");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn","usage":{"costUsd":1.5}}""");
        var self = await feed.Finals.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Null(self.ProviderMessageId);
        Assert.Equal(0.5m, self.CostUsd); // TurnEnded precedes UsageReported inside the real reader list
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"background","name":"exec_command","status":"completed"}""");
        await agent.CloseAsync();
        Assert.Equal(1, _database.QueryFirst("SELECT COUNT(*) FROM engine_usage", r => r.Long(0), default));
    }
    [Fact]
    public async Task Owning_release_is_published_only_after_original_execution_closes_and_completes()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, _db.Folder, _time));
        var feed = new MeasuringFeed(new EngineFeed(runtime, _log, _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: feed);
        var host = registry.For(_session.SessionId); var run = host.Send("work");
        var original = host.CurrentTurn;
        Assert.Equal(run, original.RunId);
        var agent = await _launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "finished");
        var close = await agent.ReadAsync("session/close");
        Assert.False(original.Done.IsCompleted);
        Assert.False(feed.Releases.Reader.TryRead(out _));
        await agent.ReplyAsync(close, "{}");
        await original.Done.WaitAsync(Wait);
        var released = await feed.Releases.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(run, released.Run); Assert.Equal(_session.SessionId, released.Observed.SessionId);
        Assert.False(host.IsOpen); Assert.False(feed.Releases.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Completed_execution_release_survives_a_new_turn_reserved_before_original_close_finishes()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, _db.Folder, _time));
        var feed = new MeasuringFeed(new EngineFeed(runtime, _log, _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: feed);
        var host = registry.For(_session.SessionId); var first = host.Send("first");
        var original = host.CurrentTurn;
        var agent = await _launcher.NextAsync(); await agent.StartAsync();
        await agent.FinishAsync(await agent.ReadAsync("session/prompt"), """{"stopReason":"end_turn"}""");
        var close = await agent.ReadAsync("session/close");
        Assert.False(original.Done.IsCompleted);
        var second = host.Send("second"); var successor = host.CurrentTurn;
        Assert.NotEqual(first, second); Assert.Equal(second, successor.RunId);
        await agent.ReplyAsync(close, "{}");
        await original.Done.WaitAsync(Wait);
        var published = feed.Releases.Reader.TryRead(out var released);
        await agent.ReplyAsync(await agent.ReadAsync("session/resume"), """{"configOptions":[]}""");
        await agent.FinishAsync(await agent.ReadAsync("session/prompt"), """{"stopReason":"end_turn"}""");
        await agent.CloseAsync(); await successor.Done.WaitAsync(Wait);
        Assert.True(published);
        Assert.Equal(first, released.Run); Assert.Equal(original.RunId, released.Run);
        Assert.Equal(_session.SessionId, released.Observed.SessionId);
    }

    [Fact]
    public async Task Kept_background_release_waits_for_close_and_uses_the_last_actual_self_started_execution()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, _db.Folder, _time));
        var feed = new MeasuringFeed(new EngineFeed(runtime, _log, _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: feed);
        var host = registry.For(_session.SessionId); var prompted = host.Send("background work");
        var agent = await _launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync(Background);
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "finished");
        await host.CurrentTurn.Done.WaitAsync(Wait);
        Assert.True(host.IsOpen); Assert.False(feed.Releases.Reader.TryRead(out _));
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"running"}""");
        await agent.TextAsync("continued background result");
        var continued = (await RowsUntil(row => row.Kind == "delta"))[^1].RunId;
        Assert.NotEqual(prompted, continued);
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "finished");
        await agent.UpdateAsync(NoBackground);
        var close = await agent.ReadAsync("session/close");
        Assert.False(feed.Releases.Reader.TryRead(out _));
        await agent.ReplyAsync(close, "{}");
        var released = await feed.Releases.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(continued, released.Run); Assert.Equal(_session.SessionId, released.Observed.SessionId);
        Assert.False(host.IsOpen); Assert.False(feed.Releases.Reader.TryRead(out _));
    }

}
