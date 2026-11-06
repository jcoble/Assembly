using Assembly.Application.Common.Database;
using Assembly.Application.Common.Calls;
using Assembly.Tests.Engine.Delivery;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

public sealed class SessionHandoffReleaseTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Display_idle_without_an_actual_open_never_supplies_release_evidence()
    {
        await using var f = new AdapterDeliveryFixture();
        var host = f.Hosts.For(f.Session.SessionId);
        Assert.Null(host.CaptureHandoffRelease(0, FakeLink.AcpSessionId));
        Assert.Equal(0, f.Launcher.Launches);
    }

    [Fact]
    public async Task Actual_turn_capture_is_epoch_and_operation_bound_and_release_disposes_the_exact_adapter()
    {
        await using var f = new AdapterDeliveryFixture();
        var host = f.Hosts.For(f.Session.SessionId);
        var sending = host.SendAsync("release fixture", "release-client", null, default);
        var agent = await f.Launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Null(host.CaptureHandoffRelease(0, FakeLink.AcpSessionId));
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await sending.WaitAsync(Wait);
        await agent.CloseAsync();
        await host.CurrentTurn.Done.WaitAsync(Wait);

        Assert.Null(host.CaptureHandoffRelease(1, FakeLink.AcpSessionId));
        Assert.Null(host.CaptureHandoffRelease(0, "another-public-id"));
        var capture = Assert.IsType<Assembly.Application.Features.Chat.Sessions.SessionHost.HandoffReleaseCapture>(
            host.CaptureHandoffRelease(0, FakeLink.AcpSessionId));
        Assert.Equal(f.Session.SessionId, capture.SessionId);
        Assert.Equal(0, capture.Epoch);
        Assert.Equal(FakeLink.AcpSessionId, capture.AcpSessionId);
        Assert.False(string.IsNullOrEmpty(capture.OperationId));
        Assert.False(agent.Disposed);

        await host.ReleaseForHandoffAsync(capture, default).WaitAsync(Wait);

        Assert.True(agent.Disposed);
        Assert.True(agent.Exited.IsCompletedSuccessfully);
        Assert.Equal("idle", f.Db.QueryFirst("SELECT state FROM engine_session WHERE id=$id", row => row.Text(0), default,
            new SqlArg("$id", f.Session.SessionId)));
    }

    [Fact]
    public async Task A_dead_provider_is_not_positive_clean_execution_evidence()
    {
        await using var f = new AdapterDeliveryFixture();
        var host = f.Hosts.For(f.Session.SessionId);
        var sending = host.SendAsync("lost execution", "lost", null, default);
        var agent = await f.Launcher.NextAsync(); await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        agent.Crash();
        await sending.WaitAsync(Wait);
        await host.CurrentTurn.Done.WaitAsync(Wait);

        Assert.Null(host.CaptureHandoffRelease(0, FakeLink.AcpSessionId));
    }

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
