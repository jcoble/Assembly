using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class CheckoutWritesTests
{
    // Pins: Task19a shared writer; New Project companion parent-before-destination ordering.
    [Fact]
    public async Task Same_root_waits_while_an_unrelated_root_progresses_and_faults_release_the_lease()
    {
        var writes = new CheckoutWrites();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = false;
        var first = writes.RunAsync("/isolated/parent", async () => { entered.SetResult(); await release.Task; return 1; });
        await entered.Task;
        var waiting = writes.RunAsync("/isolated/parent", () => { second = true; return Task.FromResult(2); });
        Assert.False(second);
        Assert.Equal(3, await writes.RunAsync("/isolated/other", () => Task.FromResult(3)));
        release.SetResult();
        Assert.Equal(1, await first); Assert.Equal(2, await waiting);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writes.RunAsync<int>("/isolated/parent", () => throw new InvalidOperationException("owned failure")));
        Assert.Equal(4, await writes.RunAsync("/isolated/parent", () => Task.FromResult(4)));
    }

    // Pins: New Project companion one shared lease, ancestor-before-descendant.
    [Fact]
    public async Task Parent_and_destination_remain_held_until_the_actual_preparation_finishes()
    {
        var writes = new CheckoutWrites();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepare = writes.RunAsync("/isolated/parent", () => writes.RunAsync("/isolated/parent/destination", async () =>
        { entered.SetResult(); await release.Task; return 1; }));
        await entered.Task;
        var parent = writes.RunAsync("/isolated/parent", () => Task.FromResult(2));
        var destination = writes.RunAsync("/isolated/parent/destination", () => Task.FromResult(3));
        Assert.False(parent.IsCompleted); Assert.False(destination.IsCompleted);
        release.SetResult();
        Assert.Equal(1, await prepare); Assert.Equal(2, await parent); Assert.Equal(3, await destination);
    }
}
