using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class MachineForward
{
    internal async Task<CommandResult> MachineCommandAsync(RemoteMachine remote, RemoteMachine.OwnerConnectionBinding binding,
        MachineCommand command, CancellationToken ct, EngineMirror.LiveMirrorBinding? live = null)
    {
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
        var result = await remote.CallOwnerBindingAsync(binding, "foreign_ancestry",
            JsonSerializer.SerializeToElement(ancestry, EngineSocketJson.Default.ForeignAncestryArgs), ct, live).ConfigureAwait(false);
        return result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid foreign ancestry result");
    }
}
