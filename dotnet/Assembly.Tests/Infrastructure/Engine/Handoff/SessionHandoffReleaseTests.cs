using Assembly.Application.Common.Database;
using Assembly.Application.Common.Calls;
using Assembly.Tests.Engine.Delivery;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

public sealed class SessionHandoffReleaseTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_successor_operation_invalidates_the_old_capture_before_adapter_disposal()
    {
        await using var f = new AdapterDeliveryFixture();
        var host = f.Hosts.For(f.Session.SessionId);
        var sending = host.SendAsync("first", "first", null, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync();
        var first = await agent.ReadAsync("session/prompt");
        await agent.FinishAsync(first, """{"stopReason":"end_turn"}""");
        await sending.WaitAsync(Wait); await agent.CloseAsync(); await host.CurrentTurn.Done.WaitAsync(Wait);
        var capture = host.CaptureHandoffRelease(0, FakeLink.AcpSessionId)!;

        var successor = host.SendAsync("second", "second", null, default);
        await Assert.ThrowsAsync<CallFailed>(() => host.ReleaseForHandoffAsync(capture, default));
        Assert.False(agent.Disposed);
        await agent.OpenAsync();
        var second = await agent.ReadAsync("session/prompt");
        await agent.FinishAsync(second, """{"stopReason":"end_turn"}""");
        await successor.WaitAsync(Wait); await agent.CloseAsync(); await host.CurrentTurn.Done.WaitAsync(Wait);
    }
}
