using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Handoffs;
using Assembly.Application.Features.Engine.Messages;
using Assembly.Application.Features.Engine.Sessions;

namespace Assembly.Tests.Engine;

public sealed class HandoffTests
{
    private static readonly EngineActor Target = new(ActorKind.System, "target");

    [Theory]
    [InlineData("resumed", 8L, "moved")]
    [InlineData("failed", 7L, "idle")]
    public void Only_resume_moves_epoch(string outcome, long epoch, string state)
    {
        using var f = new EngineFixture(); f.SeedSession("s", epoch: 7);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        Assert.True(f.Apply(new HandoffOutcome("accepted", 8, null), "h", version: 1, epoch: 8, actor: Target).Applied);
        Assert.Equal(7, f.Number("SELECT epoch FROM engine_session WHERE id='s'"));
        Assert.Equal("local", f.Text("SELECT holder FROM engine_session WHERE id='s'"));
        Assert.True(f.Apply(new HandoffOutcome(outcome, 8, outcome == "failed" ? "refused" : null),
            "h", version: 2, epoch: 8, actor: Target).Applied);
        Assert.Equal(epoch, f.Number("SELECT epoch FROM engine_session WHERE id='s'"));
        Assert.Equal(state, f.State("engine_session", "s"));
        Assert.Equal(outcome == "resumed" ? "target" : "local", f.Text("SELECT holder FROM engine_session"));
        Assert.Null(f.Text("SELECT reserved_epoch FROM engine_session"));
        Assert.Equal(outcome == "failed" ? "refused" : null, f.Text("SELECT reason FROM engine_handoff"));
        Assert.Equal(outcome, f.State("engine_handoff", "h"));
        Assert.Equal(2, f.Number("SELECT version FROM engine_session"));
        Assert.Equal(3, f.Number("SELECT version FROM engine_handoff"));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void Fence_returns_reserved_epoch(long epoch)
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        var id = Guid.NewGuid();
        var requested = f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7, commandId: id);
        Assert.True(requested.Applied); Assert.Equal(8, requested.Epoch);
        f.Reopen();
        Assert.Equal(requested, f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7, commandId: id));
        var events = f.Count("engine_event"); var effects = f.Count("engine_outbox");
        foreach (SessionCommand command in new SessionCommand[] { new StopSession(), new SetSessionState("idle", null, null) })
        {
            var result = f.Apply(command, "s", version: 1, epoch: epoch, actor: Target);
            Assert.Equal("handing_off", result.Error); Assert.Equal(8, result.Epoch);
        }
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(effects, f.Count("engine_outbox"));
        Assert.Equal("handing_off", f.State("engine_session", "s"));
        Assert.Equal(8, f.Number("SELECT reserved_epoch FROM engine_session"));
        Assert.Equal("handoff", f.Text("SELECT kind FROM engine_outbox"));
        var payload = f.Text("SELECT payload_json FROM engine_outbox")!;
        Assert.Equal(new HandoffTransfer("h", "s", "target", 8), JsonSerializer.Deserialize(payload, EngineJson.Default.HandoffTransfer));
        Assert.Equal(new RequestHandoff("s", "target"), JsonSerializer.Deserialize(
            JsonSerializer.Serialize<EngineCommand>(new RequestHandoff("s", "target"), EngineJson.Default.EngineCommand),
            EngineJson.Default.EngineCommand));
        Assert.Equal(new HandoffOutcome("accepted", 8, null), JsonSerializer.Deserialize(
            JsonSerializer.Serialize<EngineCommand>(new HandoffOutcome("accepted", 8, null), EngineJson.Default.EngineCommand),
            EngineJson.Default.EngineCommand));
    }

    [Fact]
    public void Inflight_drains_before_transfer()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.True(f.Apply(new SendMessage(null, "s", "hello", "prompt"), "m", epoch: 7).Applied);
        f.Db.Writer.Write("test.inflight", s => s.Execute("UPDATE engine_outbox SET state='inflight' WHERE kind='send'"));
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        var blocked = f.Apply(new HandoffOutcome("accepted", 8, null), "h", version: 1, epoch: 8, actor: Target);
        Assert.Equal("unavailable", blocked.Error); Assert.Equal(8, blocked.Epoch);
        Assert.Equal("requested", f.State("engine_handoff", "h"));
        Assert.Equal("inflight", f.Text("SELECT state FROM engine_outbox WHERE kind='send'"));
        Assert.True(f.Apply(new MessageOutcome("delivered_to_turn", "turn", null), "m", version: 1, epoch: 7, actor: Target).Applied);
        f.Db.Writer.Write("test.drain", s => s.Execute("UPDATE engine_outbox SET state='done' WHERE kind='send'"));
        Assert.True(f.Apply(new HandoffOutcome("accepted", 8, null), "h", version: 1, epoch: 8, actor: Target).Applied);
    }

    [Fact]
    public void Earlier_handoff_effect_must_drain_but_current_transfer_may_acknowledge()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        f.Db.Writer.Write("test.inflight", s => s.Execute("UPDATE engine_outbox SET state='inflight'"));
        Assert.True(f.Apply(new HandoffOutcome("failed", 8, "refused"), "h", version: 1, epoch: 8, actor: Target).Applied);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h2", version: 2, epoch: 7).Applied);
        var result = f.Apply(new HandoffOutcome("accepted", 8, null), "h2", version: 1, epoch: 8, actor: Target);
        Assert.Equal("unavailable", result.Error);
        f.Db.Writer.Write("test.drain", s => s.Execute("UPDATE engine_outbox SET state=CASE WHEN state='inflight' THEN 'done' ELSE 'inflight' END"));
        Assert.True(f.Apply(new HandoffOutcome("accepted", 8, null), "h2", version: 1, epoch: 8, actor: Target).Applied);
    }

    [Fact]
    public void Wrong_outcome_identity_is_refused()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        foreach (var actor in new[] { new EngineActor(ActorKind.System, "handoff"), new(ActorKind.System, "local"), new(ActorKind.Window, "target") })
            Assert.Equal("forbidden", f.Apply(new HandoffOutcome("accepted", 8, null), "h", version: 1, epoch: 8, actor: actor).Error);
        foreach (var (contextEpoch, nextEpoch) in new[] { (7L, 8L), (8L, 9L) })
        {
            var result = f.Apply(new HandoffOutcome("accepted", nextEpoch, null), "h", version: 1, epoch: contextEpoch, actor: Target);
            Assert.Equal("stale_epoch", result.Error); Assert.Equal(8, result.Epoch);
        }
        Assert.Equal("version_conflict", f.Apply(new HandoffOutcome("accepted", 8, null), "h", epoch: 8, actor: Target).Error);
        var duplicate = f.Apply(new RequestHandoff("s", "other"), "h2", version: 1, epoch: 7);
        Assert.Equal("handing_off", duplicate.Error); Assert.Equal(8, duplicate.Epoch);
        Assert.Equal(1, f.Count("engine_handoff"));
        Assert.True(f.Apply(new HandoffOutcome("accepted", 8, null), "h", version: 1, epoch: 8, actor: Target).Applied);
        foreach (var actor in new[] { new EngineActor(ActorKind.System, "handoff"), new(ActorKind.System, "local") })
            foreach (var outcome in new[] { "resumed", "failed" })
                Assert.Equal("forbidden", f.Apply(new HandoffOutcome(outcome, 8, "refused"), "h", version: 2, epoch: 8, actor: actor).Error);
    }

    public static IEnumerable<object[]> Edges()
    {
        foreach (var from in new[] { "requested", "accepted", "resumed", "failed" })
            foreach (var to in new[] { "requested", "accepted", "resumed", "failed", "unknown" })
                yield return [from, to, (from, to) is ("requested", "accepted" or "failed") or ("accepted", "resumed" or "failed")];
    }

    [Theory]
    [MemberData(nameof(Edges))]
    public void Allowed_and_invalid_edges(string from, string to, bool allowed)
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        long version = 1;
        if (from is "accepted" or "resumed")
        {
            Assert.True(f.Apply(new HandoffOutcome("accepted", 8, null), "h", version: version++, epoch: 8, actor: Target).Applied);
            if (from == "resumed")
                Assert.True(f.Apply(new HandoffOutcome("resumed", 8, null), "h", version: version++, epoch: 8, actor: Target).Applied);
        }
        if (from == "failed")
            Assert.True(f.Apply(new HandoffOutcome("failed", 8, "refused"), "h", version: version++, epoch: 8, actor: Target).Applied);
        var events = f.Count("engine_event"); var effects = f.Count("engine_outbox");
        var result = f.Apply(new HandoffOutcome(to, 8, to == "failed" ? "refused" : null), "h", version: version, epoch: 8, actor: Target);
        Assert.Equal(allowed, result.Applied);
        Assert.Equal(allowed ? null : "invalid_transition", result.Error);
        Assert.Equal(allowed ? to : from, f.State("engine_handoff", "h"));
        if (!allowed) { Assert.Equal(events, f.Count("engine_event")); Assert.Equal(effects, f.Count("engine_outbox")); }
    }

    [Fact]
    public void Old_holder_can_fail_only_before_accept_and_failure_is_durable()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        var failed = f.Apply(new HandoffOutcome("failed", 8, "cannot map"), "h", version: 1, epoch: 8,
            actor: new(ActorKind.System, "local"));
        Assert.True(failed.Applied);
        f.Reopen();
        Assert.Equal("idle", f.State("engine_session", "s"));
        Assert.Equal(7, f.Number("SELECT epoch FROM engine_session"));
        Assert.Equal("local", f.Text("SELECT holder FROM engine_session"));
        Assert.Equal("cannot map", f.Text("SELECT reason FROM engine_handoff"));
        Assert.True(f.Apply(new StopSession(), "s", version: 2, epoch: 7).Applied);
    }

    [Fact]
    public void Request_checks_session_version_epoch_arguments_and_actor()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.Equal("version_conflict", f.Apply(new RequestHandoff("s", "target"), "h", version: 1, epoch: 7).Error);
        Assert.Equal("stale_epoch", f.Apply(new RequestHandoff("s", "target"), "h", epoch: 6).Error);
        foreach (var request in new[] { new RequestHandoff("missing", "target"), new("s", " "), new("s", "local") })
            Assert.Equal("bad_args", f.Apply(request, "h", epoch: 7).Error);
        Assert.Equal("forbidden", f.Apply(new RequestHandoff("s", "target"), "h", epoch: 7, actor: Target).Error);
        Assert.Empty(f.Events()); Assert.Equal(0, f.Count("engine_outbox"));
    }
}
