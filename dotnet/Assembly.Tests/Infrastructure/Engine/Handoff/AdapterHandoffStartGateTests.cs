using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Sessions;
using Assembly.Tests.Engine.Delivery;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

public sealed class AdapterHandoffStartGateTests
{
    [Fact]
    public async Task An_unbound_adopted_payload_never_opens_a_provider_or_reconciles_as_idle()
    {
        await using var f = new AdapterDeliveryFixture();
        var binding = new HandoffStartBinding(new(f.Session.SessionId, "source-machine", "source-session", f.Runtime.Machine, 0, 1),
            "account", new string('a', 64), new string('b', 64), new string('c', 64));
        var payload = new SessionStartEffect("claude", null, "/unopened", "title", null, null, null,
            ProviderSessionId: "actual-native-id-is-unproven", Handoff: binding);
        var effect = new Effect($"start/{f.Session.SessionId}/1", "start", f.Session.SessionId, null,
            JsonSerializer.SerializeToElement(payload, EngineJson.Default.SessionStartEffect), 1);

        Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ExecuteAsync(effect, default)).Disposition);
        Assert.Equal(EffectDisposition.Unknown, (await f.Effects.ReconcileAsync(effect, default)).Disposition);
        Assert.Equal(0, f.Launcher.Launches);
    }
}
