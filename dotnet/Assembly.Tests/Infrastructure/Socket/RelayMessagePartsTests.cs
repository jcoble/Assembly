using Assembly.Infrastructure.Engine.Relays;
using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Engine.Messages;
using Assembly.Engine.Relays;
using Assembly.Infrastructure.Engine;
using Assembly.Engine;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class RelayMessagePartsTests
{
    private static CallContext Call(CancellationToken closed, string connection = "bulk") => new(default, connection, closed, Peer: new OwnerPeer());
    private static RelaySourceCapture Capture(int length = 20) => RelayDigests.Capture("A", "B", "M", "session",
        new("from", "to", new string('x', length) + "\0AFTER_NUL😀", "prompt"), new string('L', length) + "\0LABEL_SUFFIX",
        new(1, "queued", 0, false, 0, "local", null, 0, "B"));

    [Fact]
    public async Task Drop_fences_existing_stage_users_and_next_begin_can_replace_only_the_own_stage()
    {
        await using var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource();
        var capture = Capture(); var descriptor = RelayDigests.Describe(capture); var request = new RelaySendRequest(descriptor, 0);
        var probe = new RelaySendBeginReply("parts_required", null, new(descriptor.IdentitySha256, "root", null, "retry", null, false));
        using var owner = parts.Enter(Call(closed.Token));
        var handle = owner.PublishTarget(request, probe).Transfer!;
        Assert.Throws<CallFailed>(() => owner.PublishTarget(request, probe));
        owner.DropTarget(handle);
        await Assert.ThrowsAsync<CallFailed>(() => owner.Append(new(handle, RelayPartKind.Body, 0, [1])));
        var next = owner.PublishTarget(request, probe).Transfer!; Assert.NotEqual(handle, next);
        using var otherClosed = new CancellationTokenSource(); using var other = parts.Enter(Call(otherClosed.Token, "other"));
        Assert.Throws<CallFailed>(() => other.DropTarget(next));
    }

    [Fact]
    public async Task Cancellation_callback_fault_does_not_skip_join_or_disposal()
    {
        var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource();
        var operation = parts.Enter(Call(closed.Token));
        var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = operation.Token.Register(() => { callbackRan.TrySetResult(); throw new InvalidOperationException("injected cancellation callback"); });
        var disposal = parts.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
        await callbackRan.Task.WaitAsync(TimeSpan.FromSeconds(10));
        operation.Dispose(); await disposal.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Dispose_joins_unpublished_work_and_close_prevents_late_publication()
    {
        var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource();
        var operation = parts.Enter(Call(closed.Token));
        var dispose = parts.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.ThrowsAny<OperationCanceledException>(() => operation.PublishSource(Capture()));
        operation.Dispose(); await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.ThrowsAny<OperationCanceledException>(() => parts.Enter(Call(closed.Token)));
    }

    [Fact]
    public async Task Actual_closed_token_fences_published_handle_without_waiting_for_closed_hook()
    {
        await using var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource();
        string handle;
        using (var operation = parts.Enter(Call(closed.Token))) handle = operation.PublishSource(Capture()).Handle;
        closed.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => parts.Enter(Call(closed.Token)));
        using var otherClosed = new CancellationTokenSource();
        using var other = parts.Enter(Call(otherClosed.Token, "other"));
        await Assert.ThrowsAsync<CallFailed>(() => other.ReadSource(new(handle, RelayPartKind.Body, 0)));
    }

    [Fact]
    public async Task Complete_body_and_label_above_frame_limit_transfer_as_raw_segments_and_survive_admitted_close()
    {
        await using var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource();
        var capture = Capture(4 * 1024 * 1024 + 11); var descriptor = RelayDigests.Describe(capture);
        var request = new RelaySendRequest(descriptor, 0);
        var ack = new RelaySendAck(descriptor.IdentitySha256, "root", null, "retry", null, false);
        string sourceHandle, targetHandle;
        using (var operation = parts.Enter(Call(closed.Token)))
        {
            sourceHandle = operation.PublishSource(capture).Handle;
            targetHandle = operation.PublishTarget(request, new("parts_required", null, ack)).Transfer!;
        }
        foreach (var kind in new[] { RelayPartKind.Body, RelayPartKind.SenderLabel })
        {
            long offset = 0; long total;
            do
            {
                using var operation = parts.Enter(Call(closed.Token));
                var chunk = await operation.ReadSource(new(sourceHandle, kind, offset));
                Assert.InRange(chunk.Data.Length, 1, 1048576);
                await operation.Append(new(targetHandle, kind, offset, chunk.Data));
                offset = chunk.Next; total = chunk.Total;
            } while (offset < total);
        }
        PreparedRelaySend prepared;
        using (var operation = parts.Enter(Call(closed.Token))) prepared = await operation.Admit(new(request, targetHandle));
        closed.Cancel(); await parts.DisposeAsync();
        Assert.Equal(capture.Identity.Command.Text, prepared.Command.Text);
        Assert.Equal(descriptor, prepared.Source);
    }

    [Fact]
    public async Task Reorder_wrong_kind_and_hash_mismatch_never_admit_a_writer()
    {
        await using var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource();
        var capture = Capture(); var descriptor = RelayDigests.Describe(capture); var request = new RelaySendRequest(descriptor, 0);
        var ack = new RelaySendAck(descriptor.IdentitySha256, "root", null, "retry", null, false);
        using var operation = parts.Enter(Call(closed.Token));
        var handle = operation.PublishTarget(request, new("parts_required", null, ack)).Transfer!;
        await Assert.ThrowsAsync<CallFailed>(() => operation.Append(new(handle, RelayPartKind.Body, 1, [1])));
        await Assert.ThrowsAsync<CallFailed>(() => operation.Append(new(handle, (RelayPartKind)9, 0, [1])));
        await operation.Append(new(handle, RelayPartKind.Body, 0, new byte[checked((int)descriptor.Body.Utf8Bytes)]));
        await operation.Append(new(handle, RelayPartKind.SenderLabel, 0, Encoding.UTF8.GetBytes(capture.SenderLabel)));
        await Assert.ThrowsAsync<CallFailed>(() => operation.Admit(new(request, handle)));
    }

    private static (StartTitleBeginArgs Request, byte[] Facts, byte[] Title) StartParts()
    {
        var start = new StartSession("codex", null, "/folder", "Title\0suffix😀", "parent", null, null);
        var facts = StartTitlePayload.Facts(start); var title = Encoding.UTF8.GetBytes(start.Title);
        var query = new MachineStartAckArgs(Guid.NewGuid(), "A", Guid.NewGuid().ToString(), 0, 0,
            MachineStartCanonical.FromPrepared(EngineCommands.PrepareSessionTitle(start)));
        return (new(1, "B", query, new(facts.LongLength, RelayHash.Sha(facts)), new(title.LongLength, RelayHash.Sha(title))), facts, title);
    }

    [Fact]
    public async Task Start_role_competes_with_both_other_target_families_on_the_same_connection()
    {
        await using var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource();
        using var owner = parts.Enter(Call(closed.Token)); var (start, _, _) = StartParts();
        var descriptor = RelayDigests.Describe(Capture()); var relay = new RelaySendRequest(descriptor, 0);
        var probe = new RelaySendBeginReply("parts_required", null, new(descriptor.IdentitySha256, "root", null, "retry", null, false));
        var registration = EngineCommands.PrepareSessionTitle(new RegisterForeignSession("A", "codex", "Parent"));
        var foreign = new ForeignTitleRequest(ForeignTitleOperation.RegisterForeign, "B", "A", "parent",
            new(Guid.NewGuid(), "A", "parent", 0, 0, new(registration.CanonicalJournal, null, null, null)), null, 1,
            new(0, RelayHash.Sha([])), new(0, RelayHash.Sha([])));
        var foreignProbe = new ForeignTitleReply(ForeignTitleReplyKind.PartsRequired, null, null, null, null);
        var handle = owner.PublishStartTarget(start);
        Assert.Throws<CallFailed>(() => owner.PublishTarget(relay, probe));
        Assert.Throws<CallFailed>(() => owner.PublishForeignTarget(foreign, foreignProbe));
        Assert.True(owner.DropStartTarget(handle));
        var relayHandle = owner.PublishTarget(relay, probe).Transfer!;
        Assert.Throws<CallFailed>(() => owner.PublishStartTarget(start)); owner.DropTarget(relayHandle);
        var foreignHandle = owner.PublishForeignTarget(foreign, foreignProbe).Handle!;
        Assert.Throws<CallFailed>(() => owner.PublishStartTarget(start)); Assert.True(owner.DropForeignTarget(foreignHandle));
    }

    [Fact]
    public async Task Start_preparation_detaches_exact_bytes_and_payload_before_close_and_disposal()
    {
        var parts = new RelayMessageParts(); using var closed = new CancellationTokenSource();
        var (request, facts, title) = StartParts(); PreparedSessionTitleCommand prepared;
        using (var operation = parts.Enter(Call(closed.Token)))
        {
            var handle = operation.PublishStartTarget(request);
            await operation.AppendStartTitle(new(handle, StartTitlePartKind.Facts, 0, facts));
            await operation.AppendStartTitle(new(handle, StartTitlePartKind.Title, 0, title));
            using var otherClosed = new CancellationTokenSource(); using var other = parts.Enter(Call(otherClosed.Token, "other"));
            await Assert.ThrowsAsync<CallFailed>(() => other.AppendStartTitle(new(handle, StartTitlePartKind.Title, 0, title)));
            prepared = await operation.AdmitStartTitle(new(request, handle), "B");
            Assert.False(operation.DropStartTarget(handle));
        }
        closed.Cancel(); await parts.DisposeAsync();
        Assert.Equal(title, prepared.TitleUtf8);
        Assert.Equal("Title\0suffix😀", ((StartSession)prepared.Command).Title);
        Assert.Contains("suffix", prepared.StartEffectPayload!.ForExact((StartSession)prepared.Command).GetRawText());
    }

}
