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
        public SessionObservation? Capture(string id) => inner.Capture(id);
        public void ObserveBatch(IWriteScope s, SessionObservation o, string acp, IReadOnlyList<AgentEvent> events) => inner.ObserveBatch(s, o, acp, events);
        public void Asked(IWriteScope s, SessionObservation o, string id, AskPayload card, JsonRpcRequestContext request) => inner.Asked(s, o, id, card, request);
        public void Settled(IWriteScope s, SessionObservation o, string id, string how, JsonElement? answer, string reason) => inner.Settled(s, o, id, how, answer, reason);
        public void PolicyAnswered(IWriteScope s, SessionObservation o, JsonRpcRequestContext request, string method, JsonElement outcome) => inner.PolicyAnswered(s, o, request, method, outcome);
        public void Connected(IWriteScope s, SessionObservation o, string acp, bool canSteer) => inner.Connected(s, o, acp, canSteer);
        public void Lost(IWriteScope s, SessionObservation o, string reason) => inner.Lost(s, o, reason);
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
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        var feed = new MeasuringFeed(new EngineFeed(runtime, _log));
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
}
