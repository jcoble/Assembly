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
}
