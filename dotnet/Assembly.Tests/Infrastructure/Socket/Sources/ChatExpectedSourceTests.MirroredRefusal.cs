using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine.Sessions;

namespace Assembly.Tests.Infrastructure.Socket;

// A first open records a mirrored chain all or nothing, including what it would announce to subscribers.
public sealed partial class ChatExpectedSourceTests
{
    [Fact]
    public async Task A_mirrored_chain_whose_second_session_is_refused_records_and_announces_nothing()
    {
        await using var f = await RemoteFixture.Start(recorded: false, seedRemote: SeedHelper);
        // The helper's facade here names another agent, so recording it as the remote's codex helper is refused.
        f.Hosts.Mac.Sessions.Add("helper", "claude", "source-derived", "workbox-test", parentSessionId: "selected", helper: true);
        var announced = new List<string>();
        var subscriptions = new[] { SessionsFeature.Topic, SessionsFeature.RailTopic }
            .Select(topic => f.Hosts.Mac.Topics.Subscribe(topic, "selected", _ => { }, (_, _) => { lock (announced) announced.Add(topic); }))
            .ToArray();
        try
        {
            using var socket = await Open(f.Hosts.Mac.Address);
            await Call(socket, 1, "sessions", "open_target",
                Args(new SessionOpenTarget("engine", f.Source.Machine, "helper"), SessionOpenJson.Default.SessionOpenTarget));
        }
        finally { foreach (var subscription in subscriptions) f.Hosts.Mac.Topics.Unsubscribe(subscription); }

        lock (announced) Assert.Empty(announced);
        Assert.Null(f.Hosts.Mac.Sessions.Get("selected"));
        Assert.Equal(0, f.Hosts.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE id IN ('selected','helper')"));
        Assert.Equal(0, f.Hosts.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'foreign-parent/v1/%'"));
    }
}
