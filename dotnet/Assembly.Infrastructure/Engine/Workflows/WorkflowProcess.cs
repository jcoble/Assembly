using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Execution;
using Assembly.Infrastructure.Common.Processes;

namespace Assembly.Infrastructure.Engine.Workflows;

/// <summary>The engine worker owns this execution's cancellation and lifetime.</summary>
public sealed partial class WorkflowProcess(string proofRoot,
    Func<Effect, CommandExecutionIntent, CommandProcessIdentity, CancellationToken, Task<bool>> observe,
    Func<string, CancellationToken, Task<string>> fingerprint) : IWorkflowProcess
{
    public async Task<EffectReceipt> ExecuteAsync(Effect effect, bool reconcile, CancellationToken ct)
    {
        var intent = effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent);
        if (intent is null || effect.Kind != "workflow_command" || effect.SessionId is not null || effect.RunId != intent.RunId ||
            effect.OrderKey != intent.OrderKey || !OwnedPath(intent.OutputPath) || !Path.IsPathFullyQualified(intent.Cwd) || intent.PrBody is null || intent.PrBody.Generation != intent.Generation)
            return Unknown("invalid_command_effect");
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(effect.Payload.GetRawText())));
        var exitPath = intent.OutputPath + ".exit.json";
        var launchPath = intent.OutputPath + ".launch.json";
        if (File.Exists(exitPath)) return await ReadExitAsync(effect, intent, digest, exitPath, ct);
        if (File.Exists(intent.OutputPath + ".stopped.json")) return await ReadStoppedAsync(effect, intent, digest, ct);
        // No exit receipt proves no safe replay, even if the saved PID no longer exists.
        if (reconcile || File.Exists(launchPath) || File.Exists(intent.OutputPath) || File.Exists(intent.OutputPath + ".partial"))
            return Unknown("command_execution_uncertain");
        if (!await WorkflowPrBodyFile.VerifyAsync(intent.PrBody!, proofRoot, intent.RunId, ct)) return Unknown("pr_body_changed");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(intent.OutputPath)!);
        var nonce = Guid.NewGuid().ToString("N");
        var command = ProcessGroup.Supervised(new AgentCommand("/bin/sh", ["-c", intent.Script], intent.Cwd, intent.Bindings));
        var start = new ProcessStartInfo(command.FileName, command.Arguments)
        { WorkingDirectory = intent.Cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var pair in intent.Bindings) start.Environment[pair.Key] = pair.Value;
        start.Environment["ASSEMBLY_COMMAND_NONCE"] = nonce;
        await using var output = new CommandOutput(intent.OutputPath + ".partial");
        using var process = new Process { StartInfo = start };
        process.Start();
        CommandProcessIdentity? identity;
        try { identity = new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.Id, nonce); }
        catch (Exception observationError) when ((observationError is InvalidOperationException or System.ComponentModel.Win32Exception) && process.HasExited)
        {
            // A real exit can beat the OS metadata read. Record that exit, never invent a start stamp.
            identity = null;
        }
        var stdout = output.DrainAsync(process.StandardOutput.BaseStream);
        var stderr = output.DrainAsync(process.StandardError.BaseStream);
        var stopped = false;
        string? error = null;
        try
        {
            if (identity is not null)
            {
                await AtomicAsync(launchPath, JsonSerializer.SerializeToUtf8Bytes(identity, WorkflowExecutionJson.Default.CommandProcessIdentity));
                if (!await observe(effect, intent, identity, CancellationToken.None))
                { stopped = true; error = "command_authority_changed"; StopOwned(process, identity); }
            }
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { stopped = true; if (identity is not null) StopOwned(process, identity); await process.WaitForExitAsync(CancellationToken.None); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A failed metadata save does not establish nondelivery; stop only the process we just started.
            stopped = true; error = ex.Message; if (identity is not null) StopOwned(process, identity); await process.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(stdout, stderr);
        var retained = await output.FinishAsync();
        string? endFingerprint = null;
        try { endFingerprint = await fingerprint(intent.Cwd, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { error ??= ex.Message; }
        var receipt = new CommandExecutionReceipt(effect.Id, digest, identity, process.ExitCode, intent.OutputPath,
            retained.Hash, retained.Bytes, retained.Observed, retained.Truncated, stopped, error, retained.Summary, endFingerprint, identity is null ? process.Id : null);
        // Both descriptor and output are immutable after replacement; a crash between them remains uncertain.
        File.Move(intent.OutputPath + ".partial", intent.OutputPath, false);
        await AtomicAsync(exitPath, JsonSerializer.SerializeToUtf8Bytes(receipt, WorkflowExecutionJson.Default.CommandExecutionReceipt));
        return Confirmed(receipt);
    }

    private async Task<EffectReceipt> ReadExitAsync(Effect effect, CommandExecutionIntent intent, string digest, string path, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length > 32768) return Unknown("invalid_command_receipt");
            var receipt = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, ct), WorkflowExecutionJson.Default.CommandExecutionReceipt);
            if (receipt is null || receipt.EffectId != effect.Id || receipt.IntentSha256 != digest || receipt.Process is null && receipt.ExitedPid is not > 0 ||
                receipt.OutputReceiptPath != intent.OutputPath || receipt.ExitCode is null || receipt.OutputBytes > CommandOutput.Limit ||
                receipt.OutputBytes < 0 || receipt.ObservedBytes < receipt.OutputBytes || !File.Exists(intent.OutputPath))
                return Unknown("invalid_command_receipt");
            await using var file = File.OpenRead(intent.OutputPath);
            if (file.Length != receipt.OutputBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)) != receipt.OutputSha256)
                return Unknown("command_output_changed");
            return Confirmed(receipt);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return Unknown("invalid_command_receipt"); }
    }

    private bool OwnedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        var root = Path.GetFullPath(proofRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal)) return false;
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length >= root.Length - 1; parent = Path.GetDirectoryName(parent))
            if (new DirectoryInfo(parent).LinkTarget is not null) return false;
        return new FileInfo(path).LinkTarget is null;
    }

    private static void StopOwned(Process process, CommandProcessIdentity identity)
    {
        if (process.HasExited) return;
        if (process.Id != identity.Pid || process.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks)
            throw new InvalidOperationException("command_process_identity_changed");
        ProcessGroup.Kill(identity.GroupId);
        // Cancellation may beat perl's setpgrp; the same live Process remains an exact owned fallback.
        if (!process.HasExited) process.Kill(true);
    }
    private static async Task AtomicAsync(string path, byte[] bytes)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            { await file.WriteAsync(bytes); await file.FlushAsync(); file.Flush(true); }
            File.Move(temporary, path, false);
        }
        finally { File.Delete(temporary); }
    }
    private static EffectReceipt Confirmed(CommandExecutionReceipt receipt) => new(EffectDisposition.Confirmed,
        JsonSerializer.SerializeToElement(receipt, WorkflowExecutionJson.Default.CommandExecutionReceipt), null);
    private static EffectReceipt Unknown(string reason) => new(EffectDisposition.Unknown,
        JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), reason);
}
