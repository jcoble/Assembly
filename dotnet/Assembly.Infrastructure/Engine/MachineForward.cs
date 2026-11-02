using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common.Engine;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

/// <summary>Forwards through the existing owner's sockets without creating local engine state.</summary>
internal sealed partial class MachineForward(Func<string, RemoteMachine?> byMachineId)
{
    public Task<OwnerPromptPage> OwnerPromptsAsync(string machine, OwnerPromptReadArgs args,
        Func<OwnerPromptPage, int> encodedBytes, CancellationToken ct)
    {
        var remote = byMachineId(machine) ?? throw new MachineUnreachable(machine, "Remote engine machine is disconnected");
        return remote.ReadOwnerPromptPageAsync(args, encodedBytes, machine, ct);
    }

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
        if (command.Command is RegisterForeignSession && !FitsMachineFrame("machine_command", command, EngineSocketJson.Default.MachineCommand))
        {
            var owner = byMachineId(machine) ?? throw new MachineUnreachable(machine, "Remote engine machine is disconnected");
            var binding = await owner.CaptureOwnerBindingAsync(machine, ct);
            await using var lease = await owner.AcquireRelayBulkAsync(binding, ct);
            return TitleResult(await RegisterForeignTitleAsync(lease, command, ct));
        }
        var args = JsonSerializer.SerializeToElement(command, EngineSocketJson.Default.MachineCommand);
        var result = await CallAsync(machine, "engine", "machine_command", args, false, ct);
        return result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid machine command result");
    }

    public async Task<JsonElement> CallAsync(string machine, string feature, string method, JsonElement args, bool bulk, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var remote = byMachineId(machine) ?? throw new MachineUnreachable(machine, "Remote engine machine is disconnected");
        try
        {
            return feature == "engine" && method is "session_title" or "session_title_chunk"
            ? await remote.ReadOwnerPromptAsync(method, args, bulk, machine, ct)
            : await remote.CallAsync(feature, method, args, bulk, ct, machine);
        }
        catch (MachineUnreachable) when (feature == "engine" && method is "session_title" or "session_title_chunk")
        { throw new Assembly.Application.Common.Calls.CallFailed("title_changed"); }
        catch (Exception error) when (error is WebSocketException or IOException or HttpRequestException or ObjectDisposedException)
        {
            throw new MachineUnreachable(remote.Name!, $"{remote.Name}: machine socket closed", error);
        }
    }
}
