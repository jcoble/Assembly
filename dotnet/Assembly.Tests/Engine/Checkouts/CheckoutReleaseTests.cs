using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Projects;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Checkouts;

public sealed class CheckoutReleaseTests
{
    [Theory]
    [InlineData(true, "codex")]
    [InlineData(false, "codex")]
    [InlineData(true, "claude")]
    [InlineData(false, "claude")]
    public void Checkout_outcome_releases_queued_peer_without_another_transition(bool confirmed, string provider)
    {
        using var f = new EngineFixture();
        var effect = Queue(f, provider);
        var command = new CompleteEffect(Receipt(confirmed));
        var context = CheckoutTransitionTests.Context(f, effect.Id, 1);
        Assert.True(f.Apply(context, command).Applied);
        Assert.Equal(confirmed ? "ready" : "refused", f.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
        AssertReleased(f);
        var events = f.Count("engine_event");
        Assert.True(f.Apply(context, command).Applied);
        Assert.Equal(events, f.Count("engine_event"));
        AssertReleased(f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Checkout_and_peer_release_roll_back_together(bool confirmed)
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics);
        var effect = Queue(f);
        var events = f.Count("engine_event");
        var receipts = f.Count("engine_command_receipt");
        var published = 0;
        using var subscription = topics.Subscribe("__engine_committed", null, _ => { }, (_, _) =>
        {
            Assert.Equal(confirmed ? "ready" : "refused", f.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
            AssertReleased(f);
            published++;
        });
        f.Db.Writer.Write("test.fail-send", s => s.Execute("CREATE TRIGGER fail_send BEFORE INSERT ON engine_outbox WHEN NEW.kind='send' BEGIN SELECT RAISE(ABORT,'injected'); END"));
        Assert.Throws<SqliteException>(() => Complete(f, effect, confirmed));
        Assert.Equal("pending", f.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
        Assert.Equal("inflight", f.State("engine_outbox", effect.Id));
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='m'"));
        Assert.Equal(1, f.Number("SELECT version FROM engine_message WHERE id='m'"));
        Assert.Equal(events, f.Count("engine_event"));
        Assert.Equal(receipts, f.Count("engine_command_receipt"));
        Assert.Equal(0, published);
        f.Db.Writer.Write("test.allow-send", s => s.Execute("DROP TRIGGER fail_send"));
        Complete(f, effect, confirmed);
        AssertReleased(f);
        Assert.True(published > 0);
    }

    [Fact]
    public void Last_pending_checkout_releases_peers_and_other_folders_stay_queued()
    {
        using var f = new EngineFixture();
        var first = Queue(f);
        f.SeedSession("last");
        Assert.True(f.Apply(new ChangeCheckout("folder", null, null, "/elsewhere"), "last").Applied);
        var last = CheckoutTransitionTests.Claim(f);
        f.SeedSession("unrelated", state: "running");
        f.Db.Writer.Write("test.other-folder", s => s.Execute("UPDATE engine_session SET folder='/other' WHERE id='unrelated'"));
        Assert.True(f.Apply(new SendMessage(null, "unrelated", "wait", "prompt"), "other").Applied);
        Complete(f, first, true);
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='m'"));
        Complete(f, last, false);
        AssertReleased(f);
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='other'"));
    }

    [Fact]
    public void Moving_initial_checkout_releases_the_source_folder_peers()
    {
        using var f = new EngineFixture();
        f.SeedSession("peer");
        var commands = new EngineCommands(f.Machine, f.Limits, projectProjection: new ProjectProjection());
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "window"), "s", 0, 0, 1000);
        Assert.True(f.Db.Writer.Write("test.initial-checkout", s => commands.Apply(s, context,
            new StartSession("codex", "model", "/test", "Test", null, null, null,
                ProjectId: "project", Checkout: new("branch", "feature", null)))).Applied);
        var effect = CheckoutTransitionTests.Claim(f);
        Assert.True(f.Apply(new SendMessage(null, "peer", "hello", "prompt"), "m").Applied);
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='m'"));
        var receipt = new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(
            new CheckoutEffectData("/new-worktree", "feature", null, null, true), EngineJson.Default.CheckoutEffectData), null);
        Assert.True(f.Apply(CheckoutTransitionTests.Context(f, effect.Id, 1), new CompleteEffect(receipt)).Applied);
        Assert.Equal("/new-worktree", f.Text("SELECT folder FROM engine_session WHERE id='s'"));
        AssertReleased(f);
    }

    [Fact]
    public void Checkout_on_an_already_ended_session_still_releases_peers()
    {
        using var f = new EngineFixture();
        var effect = Queue(f, state: "ended");
        Complete(f, effect, false);
        AssertReleased(f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recovered_checkout_releases_peer_only_after_a_known_outcome(bool confirmed)
    {
        using var f = new EngineFixture();
        var effect = Queue(f);
        f.Reopen();
        Assert.True(f.Apply(CheckoutTransitionTests.Context(f, effect.Id, 1), new RecoverEffect()).Applied);
        Assert.Equal("pending", f.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='m'"));
        Assert.True(f.Apply(CheckoutTransitionTests.Context(f, effect.Id, 2), new ClaimEffect()).Applied);
        Assert.True(f.Apply(CheckoutTransitionTests.Context(f, effect.Id, 3), new CompleteEffect(Receipt(confirmed))).Applied);
        AssertReleased(f);
    }

    [Fact]
    public void Ending_or_stopping_a_pending_session_cannot_clear_its_checkout_fence()
    {
        using var f = new EngineFixture();
        var effect = Queue(f);
        Assert.Equal("checkout_pending", f.Apply(new SetSessionState("ended", null, null), "s", 1,
            actor: new(ActorKind.System, "adapter")).Error);
        Assert.Equal("checkout_pending", f.Apply(new StopSession(), "s", 1).Error);
        Assert.Equal("pending", f.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='m'"));
        Complete(f, effect, false);
        AssertReleased(f);
    }

    private static Effect Queue(EngineFixture f, string provider = "codex", string state = "idle")
    {
        f.SeedSession(provider: provider, state: state);
        f.SeedSession("peer", provider: provider);
        Assert.True(f.Apply(new ChangeCheckout("branch", "feature", null, null), "s").Applied);
        var effect = CheckoutTransitionTests.Claim(f);
        Assert.True(f.Apply(new SendMessage(null, "peer", "hello", "prompt"), "m").Applied);
        Assert.Equal("queued", f.State("engine_message", "m"));
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='m'"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
        return effect;
    }

    private static EffectReceipt Receipt(bool confirmed) => new(EffectDisposition.Confirmed,
        JsonSerializer.SerializeToElement(new CheckoutEffectData("/test", confirmed ? "feature" : null, null,
            confirmed ? null : "checkout_conflict", confirmed), EngineJson.Default.CheckoutEffectData), null);

    private static void Complete(EngineFixture f, Effect effect, bool confirmed) =>
        Assert.True(f.Apply(CheckoutTransitionTests.Context(f, effect.Id, 1), new CompleteEffect(Receipt(confirmed))).Applied);

    private static void AssertReleased(EngineFixture f)
    {
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id='m'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send' AND session_id='peer'"));
        var send = JsonSerializer.Deserialize(f.Text("SELECT payload_json FROM engine_outbox WHERE kind='send'")!, EngineJson.Default.DeliverMessage)!;
        Assert.Equal("peer", send.ToSessionId);
        Assert.Equal("[message from Jesse]\nhello", send.Text);
        Assert.Equal(f.Number("SELECT version FROM engine_session WHERE id='peer'"), send.RecipientVersion);
        Assert.Equal(2, f.Events().Last(e => e.EntityId == "m").Version);
    }

    private sealed class ProjectProjection : ISessionProjectProjection
    {
        public void Assigned(IWriteScope scope, string id, string? project) { }
        public void CheckoutChanged(IWriteScope scope, string id, string folder, string? branch) { }
    }
}
