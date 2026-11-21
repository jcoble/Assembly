using System.Text.Json;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Handoffs;
using Assembly.Tests.Engine.Delivery;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

public sealed class AdapterHandoffReleaseTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Uncertain_retry_cannot_redispose_an_adapter_or_invent_release_evidence()
    {
        await using var f = new AdapterDeliveryFixture();
        var agent = await CompleteTurn(f);
        var first = RequestRelease(f, "H");
        Assert.True(f.Apply(new RecoverEffect(), first.Id, f.Number("SELECT version FROM engine_outbox WHERE id=$id", first.Id)).Applied);
        var retried = f.ClaimEffect(first.Id);
        Assert.Equal(2, retried.Attempt);

        var execution = await f.Effects.ExecuteAsync(retried, default);
        var reconciliation = await f.Effects.ReconcileAsync(retried, default);

        Assert.Equal(EffectDisposition.Unknown, execution.Disposition);
        Assert.Equal(EffectDisposition.Unknown, reconciliation.Disposition);
        Assert.False(agent.Disposed);
        Assert.Equal(1, f.Launcher.Launches);
    }

    private static Effect RequestRelease(AdapterDeliveryFixture f, string handoff)
    {
        var result = f.Apply(new RequestHandoff(f.Session.SessionId, "target-engine"), handoff,
            f.Number("SELECT version FROM engine_session WHERE id=$id", f.Session.SessionId), new(ActorKind.Window, "owner"));
        Assert.True(result.Applied, result.Error);
        var actual = Assert.Single(f.Db.Query("""
            SELECT id FROM engine_outbox WHERE kind='release' AND session_id=$session
                AND json_extract(payload_json,'$.identity.handoffId')=$handoff
            """, row => row.Text(0), default, new("$session", f.Session.SessionId), new("$handoff", handoff)));
        return f.ClaimEffect(actual);
    }

    private static async Task<FakeLink> CompleteTurn(AdapterDeliveryFixture f)
    {
        var host = f.Hosts.For(f.Session.SessionId);
        var sending = host.SendAsync("captured operation", "capture", null, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await sending.WaitAsync(Wait); await agent.CloseAsync(); await host.CurrentTurn.Done.WaitAsync(Wait);
        return agent;
    }
}
