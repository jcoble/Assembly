using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Messages;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Engine.Delivery;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthObservationTests
{
    [Theory]
    [InlineData("first-null")]
    [InlineData("null")]
    [InlineData("session")]
    [InlineData("epoch")]
    public Task Engine_admission_requires_the_actual_retained_observation_before_rows_or_provider_work(string change) => Run(change, false);

    [Theory]
    [InlineData("holder")]
    [InlineData("reservation")]
    [InlineData("handoff")]
    public Task Actual_SQL_ownership_change_cannot_supply_a_local_observation_or_start_provider_work(string change) => Run(change, true);

    private static async Task Run(string change, bool ownershipChanged)
    {
        await using var f = new AdapterDeliveryFixture(); var effect = f.Claim(f.Queue("exact body", "client"));
        var intent = effect.Payload.Deserialize(EngineJson.Default.DeliverMessage)!;
        var admission = new OwningSendAdmission(effect.Id, intent.MessageId, effect.Attempt, f.Session.SessionId,
            intent.ClientId, intent.Text, null, intent.HeldEpoch!.Value, null, intent.AttachmentIds ?? [], null);
        var launched = new TaskCompletionSource<FakeLink>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Launcher.Launch = _ => { var link = new FakeLink(); launched.TrySetResult(link); return Task.FromResult<IAgentLink>(link); };
        var actualFeed = new EngineFeed(f.Runtime, TextWriter.Null, f.Time);
        if (ownershipChanged)
        {
            // The actual send was admitted before ownership changed; its host must observe the current row.
            f.Db.Writer.Write("test.changed-auth-owner", s => s.Execute(change switch
            {
                "holder" => "UPDATE engine_session SET holder='remote' WHERE id=$id",
                "reservation" => "UPDATE engine_session SET reserved_epoch=1 WHERE id=$id",
                _ => "UPDATE engine_session SET state='handing_off' WHERE id=$id"
            }, new SqlArg("$id", f.Session.SessionId)));
            Assert.Null(actualFeed.Capture(f.Session.SessionId));
        }
        ISessionFeed feed = ownershipChanged ? actualFeed : new ChangingObservation(actualFeed, change);
        await using var registry = new SessionRegistry(f.Hub, f.Sessions, f.Launcher, f.Time, TextWriter.Null, f.Work, feed: feed);
        var before = f.Rows.LastSeq(f.Session.SessionId);
        var sending = registry.For(f.Session.SessionId).SendAsync(intent.Text, intent.ClientId, null, admission, default);
        try
        {
            Assert.Same(sending, await Task.WhenAny(sending, launched.Task).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsType<SendResult.Unknown>(await sending);
            Assert.Equal(before, f.Rows.LastSeq(f.Session.SessionId)); Assert.Equal(0, f.Launcher.Launches);
            Assert.Equal("queued", f.State("engine_message", intent.MessageId)); Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id=$id", intent.MessageId));
        }
        finally
        {
            if (launched.Task.IsCompletedSuccessfully) launched.Task.Result.Crash();
            try { await sending.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    private sealed class ChangingObservation(ISessionFeed inner, string change) : ISessionFeed
    {
        private int _captures;
        public SessionObservation? Capture(string id)
        {
            var actual = inner.Capture(id); var count = Interlocked.Increment(ref _captures);
            if (count == 1 && change != "first-null") return actual;
            return change switch { "session" => actual! with { SessionId = "another-session" }, "epoch" => actual! with { Epoch = actual.Epoch + 1 }, _ => null };
        }
        public void ObserveBatch(IWriteScope s, SessionObservation o, string acp, IReadOnlyList<AgentEvent> events) => inner.ObserveBatch(s, o, acp, events);
        public Action<IWriteScope> PrepareAsked(SessionObservation o, string id, AskPayload card, JsonRpcRequestContext request) => inner.PrepareAsked(o, id, card, request);
        public Action<IWriteScope> PrepareSettled(SessionObservation o, string id, string how, JsonElement? answer, string reason) => inner.PrepareSettled(o, id, how, answer, reason);
        public Action<IWriteScope> PreparePolicyAnswered(SessionObservation o, JsonRpcRequestContext request, string method, JsonElement outcome) => inner.PreparePolicyAnswered(o, request, method, outcome);
        public void Connected(IWriteScope s, SessionObservation o, string acp, bool steer) => inner.Connected(s, o, acp, steer);
        public void Lost(IWriteScope s, SessionObservation o, string reason) => inner.Lost(s, o, reason);
        public void Replied(IWriteScope s, SessionObservation o, SessionTurnReply final) => inner.Replied(s, o, final);
        public void Recovered(IWriteScope s, RecoveredChatTurn recovered) => inner.Recovered(s, recovered);
        public bool Complete(IWriteScope s, SessionObservation o, string acp, SessionTurnFinalization final) => inner.Complete(s, o, acp, final);
    }
}
