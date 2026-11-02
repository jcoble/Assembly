using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class SharedAdapterTests
{
    [Fact]
    public async Task Detached_native_id_waits_for_exact_retired_instance_exit_while_other_borrower_finishes()
    {
        var launcher = new FakeLauncher();
        await using var pool = new SharedAdapter(launcher, "claude", TimeProvider.System, TextWriter.Null);
        var first = pool.GetAsync(new StageTimes(TimeProvider.System), CancellationToken.None);
        var old = await launcher.NextAsync();
        await SessionWarmConfigTests.Initialize(old);
        var running = await first;
        var other = await pool.GetAsync(new StageTimes(TimeProvider.System), CancellationToken.None);
        Assert.Same(running, other);
        await pool.ReleaseWarmAsync(running, "native-B");
        Assert.False(old.Disposed);
        launcher.Agents = [launcher.Agents[0] with { Args = ["new-target"] }];
        var replacement = pool.GetForSessionAsync("native-B", new StageTimes(TimeProvider.System), CancellationToken.None);
        Assert.False(replacement.IsCompleted);
        Assert.Equal(1, launcher.Launches);
        // Retiring for target replacement never kills the other active borrower.
        Assert.False(old.Disposed);
        await pool.ReleaseAsync(other);
        Assert.True(old.Exited.IsCompleted);
        var next = await launcher.NextAsync();
        await SessionWarmConfigTests.Initialize(next);
        var borrowed = await replacement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(running.TargetId, borrowed.TargetId);
        await pool.ReleaseWarmAsync(borrowed, "native-B");
        Assert.True(next.Disposed);
    }

    [Fact]
    public async Task Shutdown_fences_a_pending_native_id_borrow_instead_of_launching_after_dispose()
    {
        var launcher = new FakeLauncher();
        await using var pool = new SharedAdapter(launcher, "claude", TimeProvider.System, TextWriter.Null);
        var getting = pool.GetAsync(new StageTimes(TimeProvider.System), CancellationToken.None);
        var old = await launcher.NextAsync();
        await SessionWarmConfigTests.Initialize(old);
        var running = await getting;
        await pool.RetireAsync(running, "saved");
        var waiting = pool.GetForSessionAsync("saved", new StageTimes(TimeProvider.System), CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        await pool.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(old.Exited.IsCompleted);
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public async Task Warm_last_borrow_stops_immediately_without_idle_grace()
    {
        var launcher = new FakeLauncher();
        await using var pool = new SharedAdapter(launcher, "claude", TimeProvider.System, TextWriter.Null);
        var getting = pool.GetAsync(new StageTimes(TimeProvider.System), CancellationToken.None);
        var link = await launcher.NextAsync();
        await SessionWarmConfigTests.Initialize(link);
        var running = await getting;
        await pool.ReleaseWarmAsync(running, "saved");
        Assert.True(link.Exited.IsCompleted);
        Assert.True(link.Disposed);
    }
}
