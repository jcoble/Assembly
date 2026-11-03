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
    public async Task Release_confirms_only_the_captured_operation_and_commits_a_readable_exact_proof()
    {
        await using var f = new AdapterDeliveryFixture();
        var agent = await CompleteTurn(f);
        var effect = RequestRelease(f, "H");

        var receipt = await f.Effects.ExecuteAsync(effect, default);

        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
        var data = receipt.Data.Deserialize(EngineJson.Default.HandoffReleaseEffectData)!;
        Assert.Equal("H", data.Identity.HandoffId);
        Assert.Equal(f.Session.SessionId, data.SessionId);
        Assert.Equal(0, data.Epoch);
        Assert.Equal(FakeLink.AcpSessionId, data.ProviderSessionId);
        Assert.Equal(effect.Id, data.ReleaseEffectId);
        Assert.Equal(effect.Attempt, data.ReleaseAttempt);
        Assert.False(string.IsNullOrWhiteSpace(data.AdapterOperationId));
        Assert.True(agent.Disposed);
        Assert.True(f.Complete(effect, receipt).Applied);
        var stored = f.Db.Writer.Write("test.read-release", scope => f.Runtime.Commands.ReadHandoffRelease(scope, data.Identity));
        Assert.Equal(data, stored);
        Assert.Equal("handing_off", f.State("engine_session", f.Session.SessionId));
    }

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

    [Fact]
    public async Task Mismatched_claim_payload_refuses_before_physical_disposal()
    {
        await using var f = new AdapterDeliveryFixture();
        var agent = await CompleteTurn(f);
        var effect = RequestRelease(f, "H");
        var intent = effect.Payload.Deserialize(EngineJson.Default.HandoffReleaseEffect)!;
        var changed = effect with { Payload = JsonSerializer.SerializeToElement(intent with { ProviderSessionId = "other-public-id" }, EngineJson.Default.HandoffReleaseEffect) };

        Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ExecuteAsync(changed, default)).Disposition);
        Assert.False(agent.Disposed);
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
