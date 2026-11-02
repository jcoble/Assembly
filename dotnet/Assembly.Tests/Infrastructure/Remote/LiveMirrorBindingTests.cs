using Assembly.Infrastructure.Engine;
using Assembly.Application.Common.Database;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class LiveMirrorBindingTests
{
    [Fact]
    public async Task Unrelated_committed_progress_retains_exact_owner_and_readiness_lifetime()
    {
        await using var h = new TwoHosts(); await h.Start(); await h.StartMirror();
        var proof = await Ready(h.Mirror);
        var owner = proof.OwnerBinding;
        var firstThrough = h.Remote.Through;
        for (var i = 0; i < 20; i++)
        {
            h.Remote.Commit(1, "unrelated progress"); await h.Reached(h.Remote.Through);
            Assert.Same(proof, await h.Mirror.CaptureLiveBindingAsync(default));
            Assert.True(await h.Mirror.IsCurrentLiveBindingAsync(proof, default));
            Assert.True(await h.ConnectedMachine.IsCurrentOwnerBindingAsync(owner, default));
            Assert.False(proof.IsInvalidated);
        }
        Assert.True(h.Remote.Through > firstThrough);
    }

    [Fact]
    public async Task Disconnect_revokes_old_proof_despite_retained_live_SQL_and_same_machine_generation()
    {
        await using var h = new TwoHosts(); await h.Start(); await h.StartMirror();
        var oldMirror = h.Mirror;
        var old = await Ready(oldMirror);
        var machine = h.ConnectedMachine;
        await h.StopMirror();
        Assert.True(old.IsInvalidated);
        Assert.False(await oldMirror.IsCurrentLiveBindingAsync(old, default));
        Assert.True(old.OwnerBinding.IsInvalidated);
        Assert.False(await machine.IsCurrentOwnerBindingAsync(old.OwnerBinding, default));
        Assert.Equal("live", h.Mac.Fixture.Text("SELECT state FROM engine_mirror"));
        await h.StartMirror();
        var next = await Ready(h.Mirror);
        Assert.Equal(old.Machine, next.Machine); Assert.Equal(old.Generation, next.Generation);
        Assert.NotSame(old, next); Assert.NotSame(old.OwnerBinding, next.OwnerBinding);
        Assert.False(await h.ConnectedMachine.IsCurrentOwnerBindingAsync(old.OwnerBinding, default));
        Assert.False(await h.Mirror.IsCurrentLiveBindingAsync(old, default));
    }

    [Fact]
    public async Task Actual_socket_loss_revokes_owner_before_same_engine_reconnect_becomes_ready()
    {
        await using var h = new TwoHosts(); await h.Start(); await h.StartMirror();
        var old = await Ready(h.Mirror);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = old.OwnerBinding.Invalidated.Register(() => lost.TrySetResult());
        h.Remote.Endpoint.DropAll();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(old.IsInvalidated);
        Assert.False(await h.Mirror.IsCurrentLiveBindingAsync(old, default));
        var next = await Ready(h.Mirror);
        Assert.Equal(old.Machine, next.Machine);
        Assert.NotSame(old.OwnerBinding, next.OwnerBinding);
        Assert.False(await h.ConnectedMachine.IsCurrentOwnerBindingAsync(old.OwnerBinding, default));
    }

    [Fact]
    public async Task Committed_cursor_gap_revokes_old_proof_and_recovers_with_new_snapshot_lifetime()
    {
        await using var h = new TwoHosts(); await h.Start(); await h.StartMirror();
        var old = await Ready(h.Mirror);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = old.Invalidated.Register(() => lost.TrySetResult());
        h.Mac.Fixture.Db.Writer.Write("test.coherence-gap", s => s.Execute("UPDATE engine_mirror SET through=99"));
        h.Remote.Commit(1, "after gap");
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(old.IsInvalidated);
        var next = await Ready(h.Mirror);
        Assert.Equal(old.Machine, next.Machine);
        Assert.True(next.Generation > old.Generation);
        Assert.NotSame(old, next);
        Assert.Same(old.OwnerBinding, next.OwnerBinding);
        Assert.False(await h.Mirror.IsCurrentLiveBindingAsync(old, default));
        Assert.Equal("after gap", h.RowTitle);
    }

    [Fact]
    public async Task Partial_snapshot_cannot_qualify_and_full_committed_snapshot_then_ack_publishes_readiness()
    {
        await using var h = new TwoHosts(); await h.Start();
        for (var i = 0; i < 205; i++) h.Remote.Fixture.SeedSession($"page-{i}");
        using var barrier = new SnapshotBarrier(h.Mac.Fixture.Db.Writer);
        var starting = h.StartMirror(mirrorWriter: barrier);
        try
        {
            await barrier.Committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("snapshotting", h.Mac.Fixture.Text("SELECT state FROM engine_mirror"));
            Assert.Equal(200, h.Mac.Fixture.Count("engine_mirror_row"));
            Assert.Null(await h.Mirror.CaptureLiveBindingAsync(default));
            // A mirror writer is never allowed to hold the gate across actual owner checks.
            var owner = await h.ConnectedMachine.CaptureOwnerBindingAsync(h.Remote.Fixture.Machine, default)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await h.ConnectedMachine.IsCurrentOwnerBindingAsync(owner, default));
        }
        finally { barrier.Release.Set(); }
        await starting;
        var live = await Ready(h.Mirror);
        Assert.Equal(206, h.Mac.Fixture.Count("engine_mirror_row"));
        Assert.True(await h.Mirror.IsCurrentLiveBindingAsync(live, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_joins_the_exact_owned_invalidation_callback_task(bool owner)
    {
        await using var h = new TwoHosts(); await h.Start(); await h.StartMirror();
        var proof = await Ready(h.Mirror);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var registration = (owner ? proof.OwnerBinding.Invalidated : proof.Invalidated).Register(() =>
        {
            entered.TrySetResult();
            release.Wait();
        });
        var disposing = h.StopMirror();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(proof.IsInvalidated);
            Assert.False(disposing.IsCompleted);
        }
        finally { release.Set(); }
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class SnapshotBarrier(IAtomicWriter inner) : IAtomicWriter, IDisposable
    {
        private int _paused;
        internal TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public T Write<T>(string what, Func<IWriteScope, T> write)
        {
            var result = inner.Write(what, write);
            if (what == "engine.mirror.snapshot" && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Committed.TrySetResult();
                Release.Wait();
            }
            return result;
        }
        public void Dispose() => Release.Dispose();
    }

    private static async Task<EngineMirror.LiveMirrorBinding> Ready(EngineMirror mirror)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady() => ready.TrySetResult();
        mirror.CaughtUp += OnReady;
        try
        {
            if (await mirror.CaptureLiveBindingAsync(default) is { } live) return live;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return Assert.IsType<EngineMirror.LiveMirrorBinding>(await mirror.CaptureLiveBindingAsync(default));
        }
        finally { mirror.CaughtUp -= OnReady; }
    }
}
