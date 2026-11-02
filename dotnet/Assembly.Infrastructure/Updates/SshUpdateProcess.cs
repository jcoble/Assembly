using System.ComponentModel;
using System.Diagnostics;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Common.Processes;

namespace Assembly.Infrastructure.Updates;

// Private producer seam only. The owning capture supplies one fixed read invocation and a supervisor that
// stops and joins its remote children independently of local ssh termination. No production binding exists yet.
internal sealed record SshCliReadInvocation(AgentCommand Command, IAsyncDisposable RemoteChildren);

/// <summary>Owns one bounded SSH read process; it neither resolves remote configuration nor starts updates.</summary>
internal sealed class SshUpdateProcess(TimeProvider time)
{
    private const int StdoutLimit = 64 * 1024, StderrLimit = 16 * 1024;

    // Output remains private bytes until the remote check reader validates the safe result contract.
    internal async Task<byte[]> ReadAsync(SshCliReadInvocation captured, CancellationToken caller, CancellationToken hostStop)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), time);
        using var overflow = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(caller, hostStop, deadline.Token, overflow.Token);
        await using var remoteChildren = captured.RemoteChildren;
        stop.Token.ThrowIfCancellationRequested();
        var command = ProcessGroup.Leading(captured.Command);
        var start = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = command.WorkingDirectory
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        if (command.ReplaceEnvironment) start.Environment.Clear();
        foreach (var (key, value) in command.Environment ?? new Dictionary<string, string>()) start.Environment[key] = value;
        Process child;
        try { child = Process.Start(start) ?? throw Failure("start_failed"); }
        catch (Win32Exception) { throw Failure("start_failed"); }
        using var process = child;
        process.StandardInput.Close();
        var stdout = Drain(process.StandardOutput.BaseStream, StdoutLimit, overflow);
        var stderr = Drain(process.StandardError.BaseStream, StderrLimit, overflow);
        try
        {
            await process.WaitForExitAsync(stop.Token);
            // The process can exit while a descendant still holds its inherited streams.
            await Task.WhenAll(stdout, stderr).WaitAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            ProcessGroup.Kill(process.Id);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        caller.ThrowIfCancellationRequested(); hostStop.ThrowIfCancellationRequested();
        if (overflow.IsCancellationRequested) throw Failure("output_too_large");
        if (deadline.IsCancellationRequested) throw Failure("timeout");
        if (process.ExitCode != 0) throw Failure(process.ExitCode == 255 ? "ssh_unreachable" : "nonzero_exit");
        return await stdout;
    }

    private static async Task<byte[]> Drain(Stream input, int limit, CancellationTokenSource overflow)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (await input.ReadAsync(buffer) is var count && count != 0)
        {
            if (output.Length + count > limit) { overflow.Cancel(); return []; }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static CallFailed Failure(string reason) => new("CLI check unavailable.", reason);
}
