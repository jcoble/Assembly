using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Relays;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class MachineForward
{
    internal async Task<MachineStartResolution> SendStartAsync(FrozenMachineStartIntent intent, RemoteMachine.RelayBulkLease targetLease, CancellationToken ct)
    {
        if (!await intent.Operation.WaitAsync(0, ct).ConfigureAwait(false)) throw new CallFailed("start_operation_in_progress");
        try
        {
            if (intent.Phase != StartIssuePhase.NotIssued) throw new CallFailed("Start outcome requires acknowledgement", "unknown");
            var transport = targetLease.StartTransport();
            intent.AdmitTransport(transport.Owner, transport.Binding, transport.Live);
            // Normal issuance retains the existing complete-frame bound.
            if (!FitsMachineFrame("machine_command", intent.Command, EngineSocketJson.Default.MachineCommand))
                return await SendStartTitleCoreAsync(intent, targetLease, ct).ConfigureAwait(false);
            var result = await intent.Owner.CallOwnerBindingAsync(intent.Binding, "machine_command",
                JsonSerializer.SerializeToElement(intent.Command, EngineSocketJson.Default.MachineCommand), ct,
                intent.Live, startIntent: intent).ConfigureAwait(false);
            intent.OriginalAnswer = result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid Start result");
            return await ProbeStartCoreAsync(intent, intent.Owner, intent.Binding, targetLease, ct, intent.Live).ConfigureAwait(false);
        }
        finally { intent.Operation.Release(); }
    }

    internal async Task<MachineStartResolution> ProbeStartAsync(FrozenMachineStartIntent intent, RemoteMachine owner,
        RemoteMachine.OwnerConnectionBinding binding, RemoteMachine.RelayBulkLease targetLease, CancellationToken ct, EngineMirror.LiveMirrorBinding? live = null)
    {
        await intent.Operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!targetLease.BelongsTo(owner, binding)) throw new CallFailed("relay_connection_changed");
            return await ProbeStartCoreAsync(intent, owner, binding, targetLease, ct, live).ConfigureAwait(false);
        }
        finally { intent.Operation.Release(); }
    }
    private static async Task<MachineStartResolution> ProbeStartCoreAsync(FrozenMachineStartIntent intent, RemoteMachine owner,
        RemoteMachine.OwnerConnectionBinding binding, RemoteMachine.RelayBulkLease targetLease, CancellationToken ct, EngineMirror.LiveMirrorBinding? live)
    {
        if (binding.Machine != intent.Binding.Machine) throw new CallFailed("wrong_machine");
        var barrier = intent.IsOriginalBarrier(owner, binding) && (!intent.OriginalBulk || targetLease.HoldsOriginalBulk(intent.OriginalSocket!));
        var args = JsonSerializer.SerializeToElement(intent.Ack, EngineSocketJson.Default.MachineStartAckArgs);
        var result = barrier && intent.OriginalBulk
            ? await targetLease.CallAsync("start_ack_bulk", args, ct).ConfigureAwait(false)
            : await owner.CallOwnerBindingAsync(binding, "start_ack", args, ct, live,
                expectedConnection: barrier ? intent.OriginalSocket : null).ConfigureAwait(false);
        var evidence = result.Deserialize(EngineSocketJson.Default.MachineStartAck) ?? throw new IOException("Invalid Start acknowledgement");
        return intent.Resolve(evidence, barrier);
    }

    private static async Task<MachineStartResolution> SendStartTitleCoreAsync(FrozenMachineStartIntent intent,
        RemoteMachine.RelayBulkLease lease, CancellationToken ct)
    {
        string? handle = null;
        try
        {
            var begin = await TitleCall(lease, "start_title_begin", intent.TitleRequest, EngineSocketJson.Default.StartTitleBeginArgs,
                EngineSocketJson.Default.StartTitleBeginReply, ct);
            if (begin.Evidence.State != MachineStartAckState.Missing) return intent.Resolve(begin.Evidence, false);
            handle = begin.Handle ?? throw new CallFailed("bad_handle");
            foreach (var (kind, bytes) in new[] { (StartTitlePartKind.Facts, intent.FactsBytes), (StartTitlePartKind.Title, intent.TitleBytes) })
                for (var offset = 0; offset < bytes.Length; offset += RelaySegments.ChunkBytes)
                {
                    var data = bytes.Span.Slice(offset, Math.Min(RelaySegments.ChunkBytes, bytes.Length - offset)).ToArray();
                    var next = await TitleCall(lease, "start_title_part", new StartTitlePartArgs(handle, kind, offset, data),
                        EngineSocketJson.Default.StartTitlePartArgs, EngineSocketJson.Default.StartTitleOffsets, ct);
                    if ((kind == StartTitlePartKind.Facts ? next.FactsNext : next.TitleNext) != offset + data.LongLength) throw new CallFailed("bad_parts");
                }
            var result = await lease.CallAsync("start_title_commit", JsonSerializer.SerializeToElement(new StartTitleCommitArgs(intent.TitleRequest, handle),
                EngineSocketJson.Default.StartTitleCommitArgs), ct, intent);
            intent.OriginalAnswer = result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid Start result");
            return await ProbeStartCoreAsync(intent, intent.Owner, intent.Binding, lease, ct, intent.Live);
        }
        catch { await lease.AbortAsync().ConfigureAwait(false); throw; }
        finally
        {
            if (handle is not null)
            {
                try
                {
                    await TitleCall(lease, "start_title_drop", new Assembly.Engine.Relays.RelayHandleArgs(handle),
                    EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean, CancellationToken.None);
                }
                catch { await lease.AbortAsync().ConfigureAwait(false); }
            }
        }
    }

}
