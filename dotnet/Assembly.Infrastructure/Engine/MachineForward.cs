using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

/// <summary>Forwards through the existing owner's sockets without creating local engine state.</summary>
internal sealed class MachineForward(Func<string, RemoteMachine?> byMachineId)
{
    public async Task<CommandResult> CommandAsync(string machine, WindowCommand command, CancellationToken ct)
    {
        var args = JsonSerializer.SerializeToElement(command with { Machine = null }, EngineSocketJson.Default.WindowCommand);
        var result = await CallAsync(machine, "engine", "command", args, false, ct);
        return result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid engine command result");
    }

    public async Task<StartLocation> StartLocationAsync(string machine, string parentSessionId, CancellationToken ct)
    {
        var args = JsonSerializer.SerializeToElement(new StartLocationArgs(parentSessionId), EngineSocketJson.Default.StartLocationArgs);
        var result = await CallAsync(machine, "engine", "start_location", args, false, ct);
        return result.Deserialize(EngineSocketJson.Default.StartLocation) ?? throw new IOException("Invalid start location result");
    }

    public async Task<CommandResult> MachineCommandAsync(string machine, MachineCommand command, CancellationToken ct)
    {
        var args = JsonSerializer.SerializeToElement(command, EngineSocketJson.Default.MachineCommand);
        var result = await CallAsync(machine, "engine", "machine_command", args, false, ct);
        return result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid machine command result");
    }

    public async Task<JsonElement> CallAsync(string machine, string feature, string method, JsonElement args, bool bulk, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var remote = byMachineId(machine) ?? throw new MachineUnreachable(machine, "Remote engine machine is disconnected");
        try { return await remote.CallAsync(feature, method, args, bulk, ct, machine); }
        catch (Exception error) when (error is WebSocketException or IOException or HttpRequestException or ObjectDisposedException)
        {
            throw new MachineUnreachable(remote.Name!, $"{remote.Name}: machine socket closed", error);
        }
    }
}
