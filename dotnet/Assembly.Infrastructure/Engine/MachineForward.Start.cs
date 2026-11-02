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
            if (!FitsMachineFrame("machine_command", intent.Command, EngineSocketJson.Default.MachineCommand)) throw new CallFailed("frame_too_large");
            var result = await intent.Owner.CallOwnerBindingAsync(intent.Binding, "machine_command",
                JsonSerializer.SerializeToElement(intent.Command, EngineSocketJson.Default.MachineCommand), ct,
                intent.Live, startIntent: intent).ConfigureAwait(false);
            intent.OriginalAnswer = result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid Start result");
            return await ProbeStartCoreAsync(intent, intent.Owner, intent.Binding, ct, intent.Live).ConfigureAwait(false);
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
            return await ProbeStartCoreAsync(intent, owner, binding, ct, live).ConfigureAwait(false);
        }
        finally { intent.Operation.Release(); }
    }
    private static async Task<MachineStartResolution> ProbeStartCoreAsync(FrozenMachineStartIntent intent, RemoteMachine owner,
        RemoteMachine.OwnerConnectionBinding binding, CancellationToken ct, EngineMirror.LiveMirrorBinding? live)
    {
        if (binding.Machine != intent.Binding.Machine) throw new CallFailed("wrong_machine");
        var barrier = intent.IsOriginalBarrier(owner, binding);
        var result = await owner.CallOwnerBindingAsync(binding, "start_ack", JsonSerializer.SerializeToElement(intent.Ack, EngineSocketJson.Default.MachineStartAckArgs),
            ct, live, expectedConnection: barrier ? intent.OriginalSocket : null).ConfigureAwait(false);
        var evidence = result.Deserialize(EngineSocketJson.Default.MachineStartAck) ?? throw new IOException("Invalid Start acknowledgement");
        return intent.Resolve(evidence, barrier);
    }
}
