using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class EngineForwardTests
{
    [Fact]
    public async Task Pinned_normal_calls_keep_readiness_during_progress_and_reject_reset_before_payload()
    {
        await using var h = await PinnedHosts();
        var proof = await ReadyForPinned(h.Mirror); var owner = proof.OwnerBinding;
        var args = JsonSerializer.Deserialize("""{"entityKind":"session","entityId":"s"}""", EngineJson.Default.JsonElement);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(JsonValueKind.Object, (await h.ConnectedMachine.CallOwnerBindingAsync(owner, "usage", args, default, proof)).ValueKind);
            h.Remote.Commit(1, "progress"); await h.Reached(h.Remote.Through);
            Assert.Same(proof, await h.Mirror.CaptureLiveBindingAsync(default));
        }
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = proof.Invalidated.Register(() => invalidated.TrySetResult());
        h.Mac.Fixture.Db.Writer.Write("gap", s => s.Execute("UPDATE engine_mirror SET through=999"));
        h.Remote.Commit(1); await invalidated.Task.WaitAsync(Wait);
        var current = await ReadyForPinned(h.Mirror);
        Assert.Same(owner, current.OwnerBinding); Assert.NotSame(proof, current);
        var reads = h.Remote.OwnerReadCalls;
        var error = await Assert.ThrowsAsync<CallFailed>(() => h.ConnectedMachine.CallOwnerBindingAsync(owner, "usage", args, default, proof));
        Assert.Equal("mirror_changed", error.Message); Assert.Equal(reads, h.Remote.OwnerReadCalls);
        Assert.Equal(JsonValueKind.Object, (await h.ConnectedMachine.CallOwnerBindingAsync(owner, "usage", args, default, current)).ValueKind);
    }

    [Fact]
    public async Task Pinned_call_rejects_binding_from_a_different_actual_owner()
    {
        await using var first = await PinnedHosts();
        await using var second = await PinnedHosts();
        var proof = await ReadyForPinned(first.Mirror);
        var args = JsonSerializer.Deserialize("""{"entityKind":"session","entityId":"s"}""", EngineJson.Default.JsonElement);
        await Assert.ThrowsAnyAsync<Exception>(() => second.ConnectedMachine.CallOwnerBindingAsync(proof.OwnerBinding, "usage", args, default, proof));
        Assert.Equal(0, second.Remote.OwnerReadCalls);
    }

    private static async Task<TwoHosts> PinnedHosts()
    {
        var hosts = new TwoHosts();
        try
        {
            await hosts.Start();
            await hosts.ReplaceAtSameEndpoint(new(windowCalls: true));
            await hosts.StartMirror(); return hosts;
        }
        catch { await hosts.DisposeAsync(); throw; }
    }

    private static async Task<EngineMirror.LiveMirrorBinding> ReadyForPinned(EngineMirror mirror)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() => ready.TrySetResult();
        mirror.CaughtUp += Changed;
        try
        {
            if (await mirror.CaptureLiveBindingAsync(default) is { } current) return current;
            await ready.Task.WaitAsync(Wait);
            return Assert.IsType<EngineMirror.LiveMirrorBinding>(await mirror.CaptureLiveBindingAsync(default));
        }
        finally { mirror.CaughtUp -= Changed; }
    }
}
