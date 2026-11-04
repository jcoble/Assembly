using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Runs;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowLifecycleControlTests
{
    [Fact]
    public void Ended_asker_cancels_stale_needs_without_answer_effect_or_touching_other_askers()
    {
        using var f = WorkflowControlTests.Fixture(); f.SeedStep(); f.SeedSession("worker", parentId: "owner", runId: "r");
        Assert.True(f.Apply(new OpenNeed("worker", "r", null, "question", "Help", [], null, null, null), "stale", actor: new(ActorKind.System, "adapter")).Applied);
        Assert.True(f.Apply(new OpenNeed("owner", "r", null, "question", "Keep", [], null, null, null), "other", actor: new(ActorKind.System, "adapter")).Applied);
        var result = f.Apply(new SetSessionState("ended", null, null), "worker", f.Number("SELECT version FROM engine_session WHERE id='worker'"), actor: new(ActorKind.System, "adapter"));
        Assert.True(result.Applied, result.Error);
        Assert.Equal("cancelled", f.State("engine_needs_me", "stale")); Assert.Equal("open", f.State("engine_needs_me", "other"));
        Assert.Null(f.Text("SELECT answer_json FROM engine_needs_me WHERE id='stale'"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
        f.Reopen(); Assert.Equal("cancelled", f.State("engine_needs_me", "stale"));
    }

    [Fact]
    public void Ended_asker_recomputes_need_run_even_after_its_step_has_detached()
    {
        using var f = WorkflowControlTests.Fixture(); f.SeedStep(); f.SeedSession("worker", parentId: "owner", runId: "r");
        Assert.True(f.Apply(new OpenNeed("worker", "r", null, "question", "Help", [], null, null, null), "stale", actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("needs_me", f.State("engine_run", "r"));
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "worker", f.Number("SELECT version FROM engine_session WHERE id='worker'"), actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("queued", f.State("engine_run", "r"));
    }

    [Fact]
    public void Idle_owner_away_does_not_expire()
    {
        using var f = WorkflowControlTests.Fixture(); f.SeedStep(); f.Time.Advance(TimeSpan.FromDays(30));
        var result = f.Db.Writer.Write("test.expire", s => RunTransitions.ExpireLostOwner(s,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "engine.worker"), "r", 0, 0, f.Time.GetUtcNow().ToUnixTimeMilliseconds()), "r"));
        Assert.Equal("owner_not_expired", result.Error); Assert.Equal("running", f.State("engine_run", "r"));
        Assert.Null(f.Text("SELECT owner_lost_ms FROM engine_run"));
    }

    [Fact]
    public void Cancel_waits_for_all_stop_confirmations_and_effect_drain()
    {
        using var f = new EngineFixture(); f.SeedSession("owner"); f.SeedRun(ownerSessionId: "owner", state: "running");
        f.SeedSession("one", state: "running", parentId: "owner", runId: "r");
        f.SeedSession("two", state: "running", parentId: "owner", runId: "r");
        f.SeedStep("one", state: "running", sessionId: "one", attempt: 1);
        f.SeedStep("two", state: "running", sessionId: "two", attempt: 1);
        Assert.True(f.Apply(new CancelRun(), "r").Applied); Assert.Equal("cancelling", f.State("engine_run", "r"));
        Assert.Equal(2, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='stop'"));
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "one", f.Number("SELECT version FROM engine_session WHERE id='one'"), actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("cancelling", f.State("engine_run", "r"));
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "two", f.Number("SELECT version FROM engine_session WHERE id='two'"), actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("cancelling", f.State("engine_run", "r"));
        var stops = f.Db.Query("SELECT id,version FROM engine_outbox WHERE kind='stop' ORDER BY id",
            r => (Id: r.Text(0), Version: r.Long(1)), default);
        var receipt = new EffectReceipt(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new SessionEffectData("ended", null, null), EngineJson.Default.SessionEffectData), null);
        foreach (var effect in stops)
        {
            Assert.Equal("cancelling", f.State("engine_run", "r"));
            var claim = f.Apply(new ClaimEffect(), effect.Id, effect.Version, actor: new(ActorKind.System, "engine.worker")); Assert.True(claim.Applied);
            Assert.True(f.Apply(new CompleteEffect(receipt), effect.Id, claim.Version, actor: new(ActorKind.System, "engine.worker")).Applied);
        }
        Assert.Equal("cancelling", f.State("engine_run", "r"));
        var delivered = new EffectReceipt(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new MessageEffectData("next_turn", "owner-turn", null), EngineJson.Default.MessageEffectData), null);
        var sent = 0;
        while (f.Text("SELECT id FROM engine_outbox WHERE kind='send' AND state='pending' ORDER BY event_seq,id LIMIT 1") is { } id)
        {
            Assert.InRange(++sent, 1, 2);
            var claim = f.Apply(new ClaimEffect(), id, f.Number("SELECT version FROM engine_outbox WHERE id=$id", new SqlArg("$id", id)), actor: new(ActorKind.System, "engine.worker")); Assert.True(claim.Applied);
            Assert.True(f.Apply(new CompleteEffect(delivered), id, claim.Version, actor: new(ActorKind.System, "engine.worker")).Applied);
        }
        Assert.True(sent > 0);
        Assert.Equal("cancelled", f.State("engine_run", "r"));
    }
}
