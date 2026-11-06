using System.ComponentModel;
using System.Diagnostics;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Common.Processes;

namespace Assembly.Infrastructure.Updates;

// Request is a safe bounded JSON line. Production uses the helper's owned lifetime pipe and deadline;
// the optional supervisor is used for independently owned cleanup in process behavior fixtures.
internal sealed record SshCliReadInvocation(AgentCommand Command, IAsyncDisposable? RemoteChildren = null, byte[]? Request = null);

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
        var remoteChildren = captured.RemoteChildren;
        if (stop.IsCancellationRequested)
        {
            if (remoteChildren is not null) await remoteChildren.DisposeAsync();
            stop.Token.ThrowIfCancellationRequested();
        }
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
        catch (Exception error) when (error is Win32Exception or CallFailed)
        { if (remoteChildren is not null) await remoteChildren.DisposeAsync(); throw Failure("start_failed"); }
        using var process = child;
        var stdout = Drain(process.StandardOutput.BaseStream, StdoutLimit, overflow);
        var stderr = Drain(process.StandardError.BaseStream, StderrLimit, overflow);
        var cleanupConfirmed = true;
        try
        {
            if (captured.Request is not null)
            {
                if (captured.Request.Length > 4096 || captured.Request.Contains((byte)'\n')) throw Failure("invalid_request");
                await process.StandardInput.BaseStream.WriteAsync(captured.Request, stop.Token);
                await process.StandardInput.BaseStream.WriteAsync(new byte[] { (byte)'\n' }, stop.Token);
                await process.StandardInput.BaseStream.FlushAsync(stop.Token);
            }
            else process.StandardInput.Close();
            await process.WaitForExitAsync(stop.Token);
            // The process can exit while a descendant still holds its inherited streams.
            await Task.WhenAll(stdout, stderr).WaitAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            // EOF asks the remote helper to cancel and join its exact CLI children before it exits.
            process.StandardInput.Close();
            if (captured.Request is not null && !process.HasExited)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
                try { await process.WaitForExitAsync(cleanup.Token); }
                catch (OperationCanceledException) { cleanupConfirmed = false; }
            }
            ProcessGroup.Kill(process.Id);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            // Remote cleanup may close inherited streams: it must precede the final drain join.
            if (remoteChildren is not null) await remoteChildren.DisposeAsync();
            await Task.WhenAll(stdout, stderr);
        }
        if (!cleanupConfirmed) throw Failure("remote_cleanup_unconfirmed");
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
