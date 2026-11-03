using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Projects;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Engine.Checkouts;

public sealed class CheckoutTransitionTests
{
    [Fact]
    public void Request_is_one_stable_pending_effect_and_stale_or_busy_requests_change_nothing()
    {
        using var f = new EngineFixture(); f.SeedSession(); var id = Guid.NewGuid();
        var request = new ChangeCheckout("branch", "feature", null, null, ExpectedHead: new string('a', 40));
        Assert.True(f.Apply(request, "s", commandId: id).Applied);
        Assert.Equal("pending", f.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
        Assert.Equal("/test", f.Text("SELECT folder FROM engine_session WHERE id='s'"));
        Assert.Equal("checkout/" + id.ToString("D"), f.Text("SELECT id FROM engine_outbox"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox"));
        Assert.False(f.Apply(request, "s", version: 0).Applied);
        Assert.Equal("checkout_pending", f.Apply(new StopSession(), "s", version: 1).Error);
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox"));
    }

    [Fact]
    public void Running_or_pending_peer_refuses_before_an_effect_exists()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedSession("peer", "running");
        Assert.Equal("checkout_busy", f.Apply(new ChangeCheckout("branch", "feature", null, null), "s").Error);
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox"));
        f.Db.Writer.Write("test.peer-ended", scope => scope.Execute("UPDATE engine_session SET state='ended' WHERE id='peer'"));
        f.Db.Writer.Write("test.pending-peer", scope => scope.Execute("UPDATE engine_session SET checkout_state='pending' WHERE id='peer'"));
        Assert.Equal("checkout_busy", f.Apply(new ChangeCheckout("branch", "feature", null, null), "s").Error);
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox"));
    }

    [Fact]
    public void Confirmed_outcome_and_projection_roll_back_together_then_publish_peer_branch_once()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedSession("peer");
        var projection = SessionStore.Open(f.Db, f.Time, Path.GetTempPath());
        projection.Add("s", "codex", "", null!); projection.Add("peer", "codex", "", null!);
        var command = new ChangeCheckout("branch", "feature", null, null);
        Assert.True(f.Apply(command, "s").Applied); var effect = Claim(f);
        var receipt = new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(
            new CheckoutEffectData("/test", "feature", null, null, true), EngineJson.Default.CheckoutEffectData), null);
        Assert.Throws<InvalidOperationException>(() => Complete(f, effect, receipt, new ThrowingProjection(projection)));
        Assert.Equal("pending", f.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
        Assert.Equal("inflight", f.Text("SELECT state FROM engine_outbox"));
        Assert.Null(f.Text("SELECT branch FROM engine_session WHERE id='peer'"));
        Assert.Null(projection.Get("s")!.Branch); Assert.Null(projection.Get("peer")!.Branch);
        Assert.True(Complete(f, effect, receipt, projection).Applied);
        Assert.Equal("feature", projection.Get("s")!.Branch);
        Assert.Equal("feature", projection.Get("peer")!.Branch);
        Assert.Equal("feature", f.Text("SELECT branch FROM engine_session WHERE id='peer'"));
        Assert.Equal("ready", f.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
        Assert.Equal(2, f.Number("SELECT COUNT(*) FROM engine_event WHERE kind='session.checkout_changed'"));
    }

    [Fact]
    public void Initial_queue_retains_exact_start_payload_without_dispatching_it_until_confirmation()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "starting"); var context = Context(f, "s", 0);
        var projection = SessionStore.Open(f.Db, f.Time, Path.GetTempPath()); projection.Add("s", "codex", "", null!);
        var start = new SessionStartEffect("codex", "model", "/test", "Title 😀", null, null, null, "https://example.test/repo", true);
        f.Db.Writer.Write("test.initial-checkout", scope => EngineJournal.Execute(scope, context,
            JsonSerializer.Serialize(start, EngineJson.Default.SessionStartEffect), () =>
        {
            scope.Execute("UPDATE engine_session SET version=1 WHERE id='s'");
            List<EventDraft> events = []; List<EffectDraft> effects = [new("", "start", "s", null, JsonSerializer.SerializeToElement(start, EngineJson.Default.SessionStartEffect))];
            SessionTransitions.QueueInitialCheckout(scope, context, new CheckoutIntent("current", null, null), events, effects);
            Assert.Equal("checkout", Assert.Single(effects).Kind);
            return EngineJournal.Commit(scope, context, 1, 0, events, effects);
        }));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        var refused = Claim(f);
        var refusal = new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(
            new CheckoutEffectData("/test", null, null, "folder_unavailable", false), EngineJson.Default.CheckoutEffectData), null);
        Assert.True(Complete(f, refused, refusal, projection).Applied);
        Assert.True(f.Apply(new ChangeCheckout("current", null, null, null), "s", version: 2).Applied);
        var effect = Claim(f);
        Assert.Equal(context.CommandId.ToString("D"), effect.Payload.Deserialize(EngineJson.Default.CheckoutOperation)!.InitialOperationId);
        var receipt = new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(
            new CheckoutEffectData("/test", "main", null, null, true), EngineJson.Default.CheckoutEffectData), null);
        Assert.True(Complete(f, effect, receipt, projection).Applied);
        var saved = f.Db.QueryFirst("SELECT payload_json FROM engine_outbox WHERE kind='start'", row => row.Text(0), default)!;
        Assert.Equal(start, JsonSerializer.Deserialize(saved, EngineJson.Default.SessionStartEffect));
        Assert.Equal("start/checkout/" + effect.Payload.Deserialize(EngineJson.Default.CheckoutOperation)!.OperationId,
            f.Text("SELECT id FROM engine_outbox WHERE kind='start'"));
    }

    [Fact]
    public async Task One_host_checkout_writer_serializes_same_root_and_releases_other_roots()
    {
        var writes = new CheckoutWrites(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var secondEntered = false;
        var first = writes.RunAsync("/same", async () => { entered.SetResult(); await release.Task; return 1; });
        await entered.Task;
        var second = writes.RunAsync("/same", () => { secondEntered = true; return Task.FromResult(2); });
        Assert.False(secondEntered); Assert.Equal(3, await writes.RunAsync("/other", () => Task.FromResult(3)));
        release.SetResult(); Assert.Equal(1, await first); Assert.Equal(2, await second); Assert.True(secondEntered);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("forged-initial")]
    [InlineData("changed-intent")]
    [InlineData("ordinary")]
    [InlineData("extra-arguments")]
    public void Current_retry_requires_verified_original_current_and_creates_no_effect_for_forged_or_legacy_input(string invalid)
    {
        using var f = new EngineFixture(); f.SeedSession(state: invalid == "ordinary" ? "idle" : "starting");
        var id = Guid.NewGuid().ToString("D");
        var start = new SessionStartEffect("codex", "model", "/test", "Initial", null, null, null);
        var original = new CheckoutOperation(id, 0, "/test", null, null,
            new(invalid == "changed-intent" ? "branch" : "current", null, null, null), start,
            invalid == "legacy" ? null : id);
        f.Db.Writer.Write("test.unverified-carrier", scope => scope.Execute("""
            UPDATE engine_session SET checkout_state='refused',checkout_intent_json=$intent WHERE id='s'
            """, new SqlArg("$intent", JsonSerializer.Serialize(original, EngineJson.Default.CheckoutOperation))));
        var request = new ChangeCheckout("current", null, null, invalid == "extra-arguments" ? "/forged" : null);
        Assert.Equal("bad_args", f.Apply(request, "s").Error);
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox"));
        Assert.Equal("/test", f.Text("SELECT folder FROM engine_session WHERE id='s'"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_event"));
    }

    internal static CommandContext Context(EngineFixture f, string id, long version) => new(Guid.NewGuid(), f.Machine,
        new(ActorKind.System, "checkout-test"), id, version, 0, 1000);
    internal static Effect Claim(EngineFixture f)
    {
        var id = f.Text("SELECT id FROM engine_outbox WHERE kind='checkout' AND state='pending'")!;
        Assert.True(f.Apply(Context(f, id, 0), new ClaimEffect()).Applied);
        return EffectTransitions.Read(f.Db, id, default)!;
    }
    internal static CommandResult Complete(EngineFixture f, Effect effect, EffectReceipt receipt, ISessionProjectProjection projection) =>
        f.Db.Writer.Write("test.checkout-complete", scope => new EngineCommands(f.Machine, f.Limits, projectProjection: projection)
            .Apply(scope, Context(f, effect.Id, 1), new CompleteEffect(receipt)));
    private sealed class ThrowingProjection(ISessionProjectProjection actual) : ISessionProjectProjection
    {
        public void Assigned(IWriteScope scope, string id, string? project) => actual.Assigned(scope, id, project);
        public void CheckoutChanged(IWriteScope scope, string id, string folder, string? branch)
        {
            actual.CheckoutChanged(scope, id, folder, branch);
            throw new InvalidOperationException("Injected projection fault");
        }
    }
}
