using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Relays;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_Start_issue_resolves_actual_child_or_refusal_without_second_create(bool refused)
    {
        await using var h = new TwoHosts(machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await h.Start(); await h.StartMirror(); var live = await ReadyForPinned(h.Mirror);
        var forward = new MachineForward(_ => h.ConnectedMachine);
        await using var lease = await h.ConnectedMachine.AcquireRelayBulkAsync(live.OwnerBinding, default, live);
        var parent = Guid.NewGuid().ToString();
        Assert.True((await forward.ForeignAncestryTitlesAsync(lease, new(h.Mac.Runtime.Machine, parent,
            [new(parent, h.Mac.Runtime.Machine, "codex", "Parent", null)]), default)).Result!.Applied);
        var command = new MachineCommand(Guid.NewGuid(), h.Mac.Runtime.Machine, Guid.NewGuid().ToString(), 0, 0,
            new StartSession("codex", null, Path.GetTempPath(), refused ? "" : "Child\0suffix", parent, null, null, ManagedCheckout: false));
        var intent = FrozenMachineStartIntent.Create(h.ConnectedMachine, live.OwnerBinding, command, live);
        var missing = await forward.ProbeStartAsync(intent, h.ConnectedMachine, live.OwnerBinding, lease, default, live);
        Assert.Equal(MachineStartAckState.Missing, missing.Evidence.State); Assert.Equal(StartMissingProof.NotIssued, missing.MissingProof);
        var answer = await forward.SendStartAsync(intent, lease, default);
        Assert.Equal(refused ? MachineStartAckState.Refused : MachineStartAckState.Accepted, answer.Evidence.State);
        Assert.Equal(refused ? null : command.EntityId, answer.Evidence.Child?.Id);
        var replay = await forward.ProbeStartAsync(intent, h.ConnectedMachine, live.OwnerBinding, lease, default, live);
        Assert.Equal(answer.Evidence.Recorded, replay.Evidence.Recorded);
        await Assert.ThrowsAsync<CallFailed>(() => forward.SendStartAsync(intent, lease, default));
        Assert.Equal(refused ? 0 : 1, h.Remote.Fixture.Count("engine_outbox"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_ordered_Start_missing_is_safe_only_on_the_original_live_socket(bool replace)
    {
        await using var h = new TwoHosts(machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await h.Start(); await h.StartMirror(); var live = await ReadyForPinned(h.Mirror);
        var forward = new MachineForward(_ => h.ConnectedMachine);
        await using var lease = await h.ConnectedMachine.AcquireRelayBulkAsync(live.OwnerBinding, default, live);
        var parent = Guid.NewGuid().ToString();
        Assert.True((await forward.ForeignAncestryTitlesAsync(lease, new(h.Mac.Runtime.Machine, parent,
            [new(parent, h.Mac.Runtime.Machine, "codex", "Parent", null)]), default)).Result!.Applied);
        var request = new MachineCommand(Guid.NewGuid(), h.Mac.Runtime.Machine, Guid.NewGuid().ToString(), 0, 0,
            new StartSession("codex", null, Path.GetTempPath(), "Child", parent, null, null));
        var intent = FrozenMachineStartIntent.Create(h.ConnectedMachine, live.OwnerBinding, request, live);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CallContext? actualCall = null;
        h.Remote.MachineWriting = async (_, call) =>
        {
            actualCall = call;
            entered.TrySetResult();
            try { await new TaskCompletionSource().Task.WaitAsync(call.Cancellation); }
            catch (OperationCanceledException) when (call.Cancellation.IsCancellationRequested) { cancelled.TrySetResult(); throw; }
        };
        using var stop = new CancellationTokenSource();
        var sending = forward.SendStartAsync(intent, lease, stop.Token);
        await intent.WhenFrameOrdered.WaitAsync(Wait); await entered.Task.WaitAsync(Wait);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Equal(intent.OriginalCallId, actualCall!.ReplyId);
        Assert.False(live.OwnerBinding.IsInvalidated);
        await h.ConnectedMachine.CallOwnerBindingAsync(live.OwnerBinding, "start_location",
            JsonSerializer.SerializeToElement(new StartLocationArgs(parent), EngineSocketJson.Default.StartLocationArgs), default, live);
        Assert.True(actualCall.Cancellation.IsCancellationRequested, "Actual server call was not cancelled after the ordered normal round trip");
        await cancelled.Task.WaitAsync(Wait);
        h.Remote.MachineWriting = null;
        if (replace)
        {
            h.Remote.Endpoint.DropAll(); h.Remote.Commit(1); await h.Reached(h.Remote.Through);
            var current = await ReadyForPinned(h.Mirror);
            Assert.NotSame(live.OwnerBinding, current.OwnerBinding);
            await lease.DisposeAsync();
            await using var fresh = await h.ConnectedMachine.AcquireRelayBulkAsync(current.OwnerBinding, default, current);
            var unknown = await forward.ProbeStartAsync(intent, h.ConnectedMachine, current.OwnerBinding, fresh, default, current);
            Assert.Equal(MachineStartAckState.Unknown, unknown.Evidence.State);
            Assert.Equal(StartMissingProof.DetachedUnknown, unknown.MissingProof);
            await Assert.ThrowsAsync<CallFailed>(() => forward.SendStartAsync(intent, fresh, default));
        }
        else
        {
            var missing = await forward.ProbeStartAsync(intent, h.ConnectedMachine, live.OwnerBinding, lease, default, live);
            Assert.Equal(MachineStartAckState.Missing, missing.Evidence.State);
            Assert.Equal(StartMissingProof.SameOriginalOrderedBarrier, missing.MissingProof);
            // This lower-level fixture supplies the next admission explicitly; the coordinator owns fresh source preflight.
            Assert.Equal(MachineStartAckState.Accepted, (await forward.SendStartAsync(intent, lease, default)).Evidence.State);
        }
        Assert.Equal(replace ? 0 : 1, h.Remote.Fixture.Count("engine_outbox"));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("prepared")]
    [InlineData("committed")]
    public async Task Ordered_Start_probe_waits_for_original_turn_and_prevents_a_late_queued_send(string stage)
    {
        var committed = stage != "absent";
        await using var h = new TwoHosts(machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await h.Start(); await h.StartMirror(); var live = await ReadyForPinned(h.Mirror);
        var forward = new MachineForward(_ => h.ConnectedMachine);
        await using var lease = await h.ConnectedMachine.AcquireRelayBulkAsync(live.OwnerBinding, default, live);
        var parent = Guid.NewGuid().ToString();
        Assert.True((await forward.ForeignAncestryTitlesAsync(lease, new(h.Mac.Runtime.Machine, parent,
            [new(parent, h.Mac.Runtime.Machine, "codex", "Parent", null)]), default)).Result!.Applied);
        var request = new MachineCommand(Guid.NewGuid(), h.Mac.Runtime.Machine, Guid.NewGuid().ToString(), 0, 0,
            new StartSession("codex", null, Path.GetTempPath(), "Child", parent, null, null));
        var intent = FrozenMachineStartIntent.Create(h.ConnectedMachine, live.OwnerBinding, request, live);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stage == "prepared") h.Remote.MachinePrepared = (_, _) => { held.TrySetResult(); return release.Task; };
        else h.Remote.WriteCommitted = what =>
        {
            if (what == (committed ? "engine/machine_command" : "engine/start_ack"))
            { held.TrySetResult(); release.Task.WaitAsync(Wait).GetAwaiter().GetResult(); }
        };
        try
        {
            using var stop = new CancellationTokenSource();
            if (committed)
            {
                var original = forward.SendStartAsync(intent, lease, stop.Token);
                await held.Task.WaitAsync(Wait); stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
            }
            var probe = forward.ProbeStartAsync(intent, h.ConnectedMachine, live.OwnerBinding, lease, default, live);
            if (!committed) await held.Task.WaitAsync(Wait);
            await h.ConnectedMachine.CallOwnerBindingAsync(live.OwnerBinding, "start_location",
                JsonSerializer.SerializeToElement(new StartLocationArgs(parent), EngineSocketJson.Default.StartLocationArgs), default, live);
            Assert.False(probe.IsCompleted);
            var error = await Assert.ThrowsAsync<CallFailed>(() => forward.SendStartAsync(intent, lease, default));
            Assert.Equal("start_operation_in_progress", error.Message);
            release.TrySetResult(); var answer = await probe.WaitAsync(Wait);
            Assert.Equal(committed ? MachineStartAckState.Accepted : MachineStartAckState.Missing, answer.Evidence.State);
            Assert.Equal(committed ? StartMissingProof.None : StartMissingProof.NotIssued, answer.MissingProof);
            Assert.Equal(committed ? request.EntityId : null, answer.Evidence.Child?.Id);
            Assert.Equal(committed ? 1 : 0, h.Remote.Fixture.Count("engine_outbox"));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Lost_Start_answer_after_commit_resolves_exact_owning_child_on_replacement_without_create()
    {
        await using var h = new TwoHosts(machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await h.Start(); await h.StartMirror(); var live = await ReadyForPinned(h.Mirror);
        var forward = new MachineForward(_ => h.ConnectedMachine);
        await using var lease = await h.ConnectedMachine.AcquireRelayBulkAsync(live.OwnerBinding, default, live);
        var parent = Guid.NewGuid().ToString();
        Assert.True((await forward.ForeignAncestryTitlesAsync(lease, new(h.Mac.Runtime.Machine, parent,
            [new(parent, h.Mac.Runtime.Machine, "codex", "Parent", null)]), default)).Result!.Applied);
        var request = new MachineCommand(Guid.NewGuid(), h.Mac.Runtime.Machine, Guid.NewGuid().ToString(), 0, 0,
            new StartSession("codex", null, Path.GetTempPath(), "Child\0original", parent, null, null));
        var intent = FrozenMachineStartIntent.Create(h.ConnectedMachine, live.OwnerBinding, request, live);
        h.Remote.WriteCommitted = what => { if (what == "engine/machine_command") h.Remote.Endpoint.DropAll(); };
        await Assert.ThrowsAnyAsync<Exception>(() => forward.SendStartAsync(intent, lease, default));
        h.Remote.WriteCommitted = null; h.Remote.Commit(1); await h.Reached(h.Remote.Through);
        var current = await ReadyForPinned(h.Mirror); Assert.NotSame(live.OwnerBinding, current.OwnerBinding);
        await lease.DisposeAsync();
        await using var fresh = await h.ConnectedMachine.AcquireRelayBulkAsync(current.OwnerBinding, default, current);
        var resolved = await forward.ProbeStartAsync(intent, h.ConnectedMachine, current.OwnerBinding, fresh, default, current);
        Assert.Equal(MachineStartAckState.Accepted, resolved.Evidence.State); Assert.Equal(request.EntityId, resolved.Evidence.Child!.Id);
        Assert.Equal(StartMissingProof.None, resolved.MissingProof);
        await Assert.ThrowsAsync<CallFailed>(() => forward.SendStartAsync(intent, fresh, default));
        Assert.Equal(1, h.Remote.Fixture.Count("engine_outbox"));
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
