using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Execution;

namespace Assembly.Infrastructure.Engine.Workflows;

public sealed partial class WorkflowProcess
{
    internal async Task<EffectReceipt> StopAsync(Effect effect, CommandProcessIdentity identity, CancellationToken ct)
    {
        var i = effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent)!;
        if (!OwnedPath(i.OutputPath) || identity.Pid <= 0 || identity.GroupId != identity.Pid || identity.StartUtcTicks <= 0 ||
            !Guid.TryParseExact(identity.Nonce, "N", out _)) return Unknown("stop_identity_unverified");
        var digest = CommandStepTransitions.Digest(effect.Payload.GetRawText());
        if (File.Exists(i.OutputPath + ".exit.json")) return await ReadExitAsync(effect, i, digest, i.OutputPath + ".exit.json", ct);
        var launch = i.OutputPath + ".launch.json";
        if (new FileInfo(launch).LinkTarget is not null || !File.Exists(launch) || new FileInfo(launch).Length > 4096 ||
            JsonSerializer.Deserialize(await File.ReadAllBytesAsync(launch, ct), WorkflowExecutionJson.Default.CommandProcessIdentity) != identity)
            return Unknown("stop_identity_unverified");
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks) return Unknown("stop_identity_changed");
            StopOwned(process, identity);
            await process.WaitForExitAsync(ct);
        }
        catch (ArgumentException) { } // The original PID is positively absent; group absence is still required.
        catch (InvalidOperationException) { return Unknown("stop_identity_unverified"); }
        ct.ThrowIfCancellationRequested();
        if (SignalGroup(-identity.GroupId, 0) != -1 || Marshal.GetLastPInvokeError() != 3) return Unknown("stop_group_not_drained");
        if (File.Exists(i.OutputPath + ".exit.json")) return await ReadExitAsync(effect, i, digest, i.OutputPath + ".exit.json", ct);
        // No exit status or complete output can be reconstructed after losing the original pipe owner.
        var receipt = new CommandExecutionReceipt(effect.Id, digest, identity, null, "", "", 0, 0, false, true,
            "executor_stopped_without_exit_receipt", "", null, ExecutorAbsent: true);
        var path = i.OutputPath + ".stopped.json";
        if (!File.Exists(path)) await AtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(receipt, WorkflowExecutionJson.Default.CommandExecutionReceipt));
        return await ReadStoppedAsync(effect, i, digest, ct);
    }

    private async Task<EffectReceipt> ReadStoppedAsync(Effect effect, CommandExecutionIntent i, string digest, CancellationToken ct)
    {
        var path = i.OutputPath + ".stopped.json";
        try
        {
            if (new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length > 32768) return Unknown("invalid_stopped_receipt");
            var receipt = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, ct), WorkflowExecutionJson.Default.CommandExecutionReceipt);
            if (receipt is not { ExecutorAbsent: true, Stopped: true, ExitCode: null, Process: not null } || receipt.EffectId != effect.Id ||
                receipt.IntentSha256 != digest || receipt.OutputReceiptPath != "" || receipt.OutputSha256 != "" || receipt.ExitedPid is not null)
                return Unknown("invalid_stopped_receipt");
            return Confirmed(receipt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return Unknown("invalid_stopped_receipt"); }
    }
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SignalGroup(int pid, int signal);
}
