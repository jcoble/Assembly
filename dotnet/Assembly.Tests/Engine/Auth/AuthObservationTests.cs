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
using Assembly.Engine.Sessions;
using Assembly.Application.Common.Calls;
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

    [Theory]
    [InlineData(false, "run")]
    [InlineData(false, "step")]
    [InlineData(false, "attempt")]
    [InlineData(true, "run")]
    [InlineData(true, "step")]
    [InlineData(true, "attempt")]
    public async Task Fresh_auth_feed_fact_rejects_a_changed_retained_workflow_tuple(bool failedOpen, string field)
    {
        await using var f = new AdapterDeliveryFixture(configured: (provider, account) => provider == "claude" && account == "");
        var proof = FeedProof(f, failedOpen); var feed = new EngineFeed(f.Runtime, TextWriter.Null, f.Time);
        var observed = feed.Capture(f.Session.SessionId)!;
        var changed = field switch { "run" => observed with { RunId = "wrong-run" }, "step" => observed with { StepId = "wrong-step" }, _ => observed with { Attempt = 1 } };
        Assert.Throws<CallFailed>(() => f.Db.Writer.Write("test.changed-retained-auth-tuple", scope =>
        {
            if (failedOpen) feed.BlockFailedOpenSignInNeeded(scope, changed, proof);
            else feed.BlockSignInNeeded(scope, changed, proof.AcpSessionId!, proof);
            return 0;
        }));
        Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", row => row.Long(0), default));
        Assert.Equal(0, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_provider", row => row.Long(0), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_auth_feed_receipt_replays_before_a_later_observation_mismatch(bool failedOpen)
    {
        await using var f = new AdapterDeliveryFixture(configured: (provider, account) => provider == "claude" && account == "");
        var proof = FeedProof(f, failedOpen); var feed = new EngineFeed(f.Runtime, TextWriter.Null, f.Time);
        var observed = feed.Capture(f.Session.SessionId)!;
        void Apply(SessionObservation retained) => f.Db.Writer.Write("test.auth-tuple-replay", scope =>
        {
            if (failedOpen) feed.BlockFailedOpenSignInNeeded(scope, retained, proof);
            else feed.BlockSignInNeeded(scope, retained, proof.AcpSessionId!, proof);
            return 0;
        });
        Apply(observed);
        var version = f.Db.QueryFirst("SELECT version FROM engine_provider", row => row.Long(0), default);
        Apply(observed with { RunId = "later-run", StepId = "later-step", Attempt = 99 });
        Apply(observed with { Epoch = observed.Epoch + 1 });
        Assert.Equal(version, f.Db.QueryFirst("SELECT version FROM engine_provider", row => row.Long(0), default));
        Assert.Equal(1, f.Db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me", row => row.Long(0), default));
    }

    private static AuthTerminalProof FeedProof(AdapterDeliveryFixture f, bool failedOpen)
    {
        var effect = f.Claim(f.Queue());
        f.Rows.Commit(f.Session.SessionId, new RowConverter().Apply(new TurnStarted(1000, "actual-chat-run", "exact body", "C")));
        if (!failedOpen)
            Assert.True(f.Apply(new SetSessionState("running", "actual-acp", null), f.Session.SessionId,
                f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId)).Applied);
        using var snapshot = f.Db.OpenSnapshot(default);
        var user = f.Rows.UserDeliveryOfClient(snapshot, f.Session.SessionId, "C")!;
        return new(failedOpen ? 2 : 1, Guid.NewGuid(), Guid.NewGuid(), "actual-chat-run", failedOpen ? null : "actual-acp",
            f.Number("SELECT epoch FROM engine_session WHERE id=$id", f.Session.SessionId), "C", null,
            new(effect.Payload.GetProperty("messageId").GetString()!, effect.Id, effect.Attempt, "C", user.Seq, user.RunId!),
            failedOpen ? AuthOpenFailureStage.Initialize : null);
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
        public void BlockSignInNeeded(IWriteScope s, SessionObservation o, string acp, AuthTerminalProof p) => inner.BlockSignInNeeded(s, o, acp, p);
        public void SignInNeeded(IWriteScope s, SessionObservation o, string acp, long seq, AuthTerminalProof p) => inner.SignInNeeded(s, o, acp, seq, p);
        public void BlockFailedOpenSignInNeeded(IWriteScope s, SessionObservation o, AuthTerminalProof p) => inner.BlockFailedOpenSignInNeeded(s, o, p);
        public void FailedOpenSignInNeeded(IWriteScope s, SessionObservation o, long seq, AuthTerminalProof p) => inner.FailedOpenSignInNeeded(s, o, seq, p);
        public void RetryStarted(IWriteScope s, SessionObservation o, AuthRetryExecution e) => inner.RetryStarted(s, o, e);
        public void Replied(IWriteScope s, SessionObservation o, SessionTurnReply final) => inner.Replied(s, o, final);
        public void Recovered(IWriteScope s, RecoveredChatTurn recovered) => inner.Recovered(s, recovered);
        public void Released(SessionObservation observed, string chatRunId) => inner.Released(observed, chatRunId);
        public bool Complete(IWriteScope s, SessionObservation o, string acp, SessionTurnFinalization final) => inner.Complete(s, o, acp, final);
    }
}
