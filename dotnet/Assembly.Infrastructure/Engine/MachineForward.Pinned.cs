using Assembly.Infrastructure.Engine.Relays;
using Assembly.Engine.NeedsMe;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class MachineForward
{
    internal async Task<CommandResult> MachineCommandAsync(RemoteMachine remote, RemoteMachine.OwnerConnectionBinding binding,
        MachineCommand command, CancellationToken ct, EngineMirror.LiveMirrorBinding? live = null)
    {
        if (command.Command is RegisterForeignSession && !FitsMachineFrame("machine_command", command, EngineSocketJson.Default.MachineCommand))
        {
            await using var lease = await remote.AcquireRelayBulkAsync(binding, ct, live);
            return TitleResult(await RegisterForeignTitleAsync(lease, command, ct));
        }
        var result = await remote.CallOwnerBindingAsync(binding, "machine_command",
            JsonSerializer.SerializeToElement(command, EngineSocketJson.Default.MachineCommand), ct, live).ConfigureAwait(false);
        return result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid machine command result");
    }

    internal async Task<StartLocation> StartLocationAsync(RemoteMachine remote, RemoteMachine.OwnerConnectionBinding binding,
        string parentSessionId, CancellationToken ct, EngineMirror.LiveMirrorBinding? live = null)
    {
        var result = await remote.CallOwnerBindingAsync(binding, "start_location",
            JsonSerializer.SerializeToElement(new StartLocationArgs(parentSessionId), EngineSocketJson.Default.StartLocationArgs), ct, live).ConfigureAwait(false);
        return result.Deserialize(EngineSocketJson.Default.StartLocation) ?? throw new IOException("Invalid start location result");
    }

    internal async Task<CommandResult> ForeignAncestryAsync(RemoteMachine remote, RemoteMachine.OwnerConnectionBinding binding,
        ForeignAncestryArgs ancestry, CancellationToken ct, EngineMirror.LiveMirrorBinding? live = null)
    {
        if (!FitsMachineFrame("foreign_ancestry", ancestry, EngineSocketJson.Default.ForeignAncestryArgs))
        {
            await using var lease = await remote.AcquireRelayBulkAsync(binding, ct, live);
            return TitleResult(await ForeignAncestryTitlesAsync(lease, ancestry, ct),
                ForeignTitleDigests.Structure(binding.Machine, ancestry.FromMachine, ancestry.ParentSessionId, ancestry.RootToParent));
        }
        var result = await remote.CallOwnerBindingAsync(binding, "foreign_ancestry",
            JsonSerializer.SerializeToElement(ancestry, EngineSocketJson.Default.ForeignAncestryArgs), ct, live).ConfigureAwait(false);
        return result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid foreign ancestry result");
    }
    internal Task<RelaySourceOpened> OpenRelaySourceAsync(RemoteMachine.RelayBulkLease lease, RelaySourceReadArgs args, CancellationToken ct) =>
        TitleCall(lease, "relay_source_open", args, EngineSocketJson.Default.RelaySourceReadArgs, EngineSocketJson.Default.RelaySourceOpened, ct);
    internal Task<RelaySourcePart> ReadRelaySourcePartAsync(RemoteMachine.RelayBulkLease lease, RelaySourcePartArgs args, CancellationToken ct) =>
        TitleCall(lease, "relay_source_part", args, EngineSocketJson.Default.RelaySourcePartArgs, EngineSocketJson.Default.RelaySourcePart, ct);
    internal Task<RelaySourceDescriptor> CheckRelaySourceAsync(RemoteMachine.RelayBulkLease lease, string handle, CancellationToken ct) =>
        TitleCall(lease, "relay_source_revalidate", new RelayHandleArgs(handle), EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.RelaySourceDescriptor, ct);
    internal async Task CloseRelaySourceAsync(RemoteMachine.RelayBulkLease lease, string handle, CancellationToken ct)
    {
        try { await TitleCall(lease, "relay_source_close", new RelayHandleArgs(handle), EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean, ct); }
        catch { await lease.AbortAsync(); }
    }
    internal async Task<RelaySendAck> RelayAckAsync(RemoteMachine remote, RemoteMachine.OwnerConnectionBinding binding,
        RelaySourceDescriptor args, CancellationToken ct, EngineMirror.LiveMirrorBinding live) =>
        (await remote.CallOwnerBindingAsync(binding, "relay_send_ack", JsonSerializer.SerializeToElement(args, EngineSocketJson.Default.RelaySourceDescriptor), ct, live)).Deserialize(EngineSocketJson.Default.RelaySendAck)!;
    internal Task<RelaySendBeginReply> RelayBeginAsync(RemoteMachine.RelayBulkLease lease, RelaySendRequest args, CancellationToken ct) =>
        TitleCall(lease, "relay_send_begin", args, EngineSocketJson.Default.RelaySendRequest, EngineSocketJson.Default.RelaySendBeginReply, ct);
    internal Task<RelaySendPartAccepted> RelayPartAsync(RemoteMachine.RelayBulkLease lease, RelaySendPartArgs args, CancellationToken ct) =>
        TitleCall(lease, "relay_send_part", args, EngineSocketJson.Default.RelaySendPartArgs, EngineSocketJson.Default.RelaySendPartAccepted, ct);
    internal Task<RelaySendAck> RelayCommitAsync(RemoteMachine.RelayBulkLease lease, RelaySendCommitArgs args, CancellationToken ct) =>
        TitleCall(lease, "relay_send_commit", args, EngineSocketJson.Default.RelaySendCommitArgs, EngineSocketJson.Default.RelaySendAck, ct);
    internal async Task DropRelayTargetAsync(RemoteMachine.RelayBulkLease lease, string handle, CancellationToken ct)
    {
        try { await TitleCall(lease, "relay_send_drop", new RelayHandleArgs(handle), EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean, ct); }
        catch { await lease.AbortAsync(); }
    }
    internal async Task<CommandResult> RelayOutcomeAsync(RemoteMachine remote, RemoteMachine.OwnerConnectionBinding binding,
        RelayOutcomeArgs args, CancellationToken ct, EngineMirror.LiveMirrorBinding live) =>
        (await remote.CallOwnerBindingAsync(binding, "relay_outcome", JsonSerializer.SerializeToElement(args, EngineSocketJson.Default.RelayOutcomeArgs), ct, live)).Deserialize(EngineJson.Default.CommandResult)!;

    internal async Task<ParentNeedPendingPage> ParentNeedPendingAsync(RemoteMachine remote,
        RemoteMachine.OwnerConnectionBinding binding, ParentNeedPendingArgs args, CancellationToken ct, EngineMirror.LiveMirrorBinding live) =>
        (await remote.CallOwnerBindingAsync(binding, "parent_need_pending",
            JsonSerializer.SerializeToElement(args, EngineSocketJson.Default.ParentNeedPendingArgs), ct, live))
        .Deserialize(EngineSocketJson.Default.ParentNeedPendingPage)!;

    internal async Task<NeedAnswerAck> ProbeParentNeedAsync(RemoteMachine remote,
        RemoteMachine.OwnerConnectionBinding binding, NeedAnswerRequestDescriptor args, CancellationToken ct, EngineMirror.LiveMirrorBinding live) =>
        (await remote.CallOwnerBindingAsync(binding, "need_answer_probe",
            JsonSerializer.SerializeToElement(args, EngineSocketJson.Default.NeedAnswerRequestDescriptor), ct, live))
        .Deserialize(EngineSocketJson.Default.NeedAnswerAck)!;

    internal async Task<ParentNeedDispatchAck> DispatchParentNeedAsync(RemoteMachine remote,
        RemoteMachine.OwnerConnectionBinding binding, ParentNeedDispatchArgs args, CancellationToken ct, EngineMirror.LiveMirrorBinding live) =>
        (await remote.CallOwnerBindingAsync(binding, "parent_need_dispatch",
            JsonSerializer.SerializeToElement(args, EngineSocketJson.Default.ParentNeedDispatchArgs), ct, live))
        .Deserialize(EngineJson.Default.ParentNeedDispatchAck)!;

    internal async Task<CommandResult> CompleteParentNeedAsync(RemoteMachine remote,
        RemoteMachine.OwnerConnectionBinding binding, ParentNeedOutcomeArgs args, CancellationToken ct, EngineMirror.LiveMirrorBinding live) =>
        (await remote.CallOwnerBindingAsync(binding, "parent_need_outcome",
            JsonSerializer.SerializeToElement(args, EngineSocketJson.Default.ParentNeedOutcomeArgs), ct, live))
        .Deserialize(EngineJson.Default.CommandResult)!;

    internal async Task<ParentNeedSourceCapture> ReadParentNeedInputAsync(RemoteMachine.RelayBulkLease lease,
        string machine, string intentId, CancellationToken ct)
    {
        var opened = await ParentNeedCall(lease, "parent_need_input_open", new ParentNeedInputArgs(intentId),
            EngineSocketJson.Default.ParentNeedInputArgs, EngineSocketJson.Default.ParentNeedInputOpened, ct);
        try
        {
            using var segments = new RelaySegments(opened.Payload);
            while (segments.Length < opened.Payload.Utf8Bytes)
            {
                var part = await ParentNeedCall(lease, "parent_need_input_part", new ParentNeedInputPartArgs(opened.Handle, segments.Length),
                    EngineSocketJson.Default.ParentNeedInputPartArgs, EngineSocketJson.Default.RelaySourcePart, ct);
                if (part.Offset != segments.Length || part.Total != opened.Payload.Utf8Bytes || part.Next != part.Offset + part.Data.LongLength)
                    throw new IOException("Invalid parent need input part");
                segments.Append(part.Offset, part.Data, ct);
            }
            return ParentNeedSourceCapture.Create(machine, intentId, opened.Version, opened.State, opened.CreatedMs, segments.Complete(ct), ct);
        }
        finally
        {
            try { await ParentNeedCall(lease, "parent_need_input_close", new RelayHandleArgs(opened.Handle), EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean, CancellationToken.None); }
            catch { await lease.AbortAsync(); }
        }
    }

    internal async Task<ParentNeedCaptured> CaptureParentNeedAsync(RemoteMachine remote,
        RemoteMachine.OwnerConnectionBinding binding, ParentNeedSource source, CancellationToken ct, EngineMirror.LiveMirrorBinding live) =>
        (await remote.CallOwnerBindingAsync(binding, "need_answer_capture",
            JsonSerializer.SerializeToElement(source, EngineJson.Default.ParentNeedSource), ct, live))
        .Deserialize(EngineSocketJson.Default.ParentNeedCaptured)!;

    internal async Task<CommandResult> PrepareParentNeedAsync(RemoteMachine.RelayBulkLease lease, string intentId,
        long version, ParentNeedPrepared payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, EngineJson.Default.ParentNeedPrepared);
        var handle = await UploadParentNeed(lease, new(ParentNeedUploadKind.Prepare, new(bytes.LongLength, RelayHash.Sha(bytes)), intentId, version), bytes, ct);
        var consumed = false;
        try
        {
            var result = await ParentNeedCall(lease, "parent_need_prepare", new RelayHandleArgs(handle), EngineSocketJson.Default.RelayHandleArgs, EngineJson.Default.CommandResult, ct);
            consumed = true; return result;
        }
        finally { if (!consumed) await DropParentNeedStage(lease, handle); }
    }
    internal async Task<NeedAnswerAck> ExecuteParentNeedAsync(RemoteMachine.RelayBulkLease lease, ParentNeedPrepared payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, EngineJson.Default.ParentNeedPrepared);
        var handle = await UploadParentNeed(lease, new(ParentNeedUploadKind.Execute, new(bytes.LongLength, RelayHash.Sha(bytes))), bytes, ct);
        var consumed = false;
        try
        {
            var result = await ParentNeedCall(lease, "need_answer_execute", new RelayHandleArgs(handle), EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.NeedAnswerAck, ct);
            consumed = true; return result;
        }
        finally { if (!consumed) await DropParentNeedStage(lease, handle); }
    }
    private static async Task<string> UploadParentNeed(RemoteMachine.RelayBulkLease lease, ParentNeedUploadArgs args, byte[] bytes, CancellationToken ct)
    {
        var opened = await ParentNeedCall(lease, "need_answer_stage_open", args, EngineSocketJson.Default.ParentNeedUploadArgs, EngineSocketJson.Default.ParentNeedUploadOpened, ct);
        try
        {
            long offset = 0;
            while (offset < bytes.LongLength)
            {
                var count = (int)Math.Min(RelaySegments.ChunkBytes, bytes.LongLength - offset);
                var part = new ParentNeedUploadPartArgs(opened.Handle, offset, bytes.AsSpan(checked((int)offset), count).ToArray());
                var accepted = await ParentNeedCall(lease, "need_answer_stage_part", part, EngineSocketJson.Default.ParentNeedUploadPartArgs, EngineSocketJson.Default.ParentNeedUploadOffset, ct);
                offset += count;
                if (accepted.Next != offset) throw new IOException("Invalid parent need upload offset");
            }
            return opened.Handle;
        }
        catch { await DropParentNeedStage(lease, opened.Handle); throw; }
    }
    private static async Task<TResult> ParentNeedCall<TArgs, TResult>(RemoteMachine.RelayBulkLease lease,
        string method, TArgs args, JsonTypeInfo<TArgs> input, JsonTypeInfo<TResult> output, CancellationToken ct)
    {
        try { return await TitleCall(lease, method, args, input, output, ct); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        { throw new IOException(method + ": " + error.Message, error); }
    }

    private static async Task DropParentNeedStage(RemoteMachine.RelayBulkLease lease, string handle)
    {
        try { await ParentNeedCall(lease, "need_answer_stage_drop", new RelayHandleArgs(handle), EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean, CancellationToken.None); }
        catch { await lease.AbortAsync(); }
    }
}
