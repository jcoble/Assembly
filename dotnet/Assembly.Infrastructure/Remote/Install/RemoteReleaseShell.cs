using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Common.Processes;

namespace Assembly.Infrastructure.Remote.Install;

/// <summary>Runs one command on a saved machine over SSH. Every stdout line goes to <c>line</c>; a failure carries the command's own output.</summary>
internal class RemoteReleaseShell
{
    private const int ErrorLimit = 16 * 1024;
    // install.sh's answer when a session started after the wait for running sessions.
    private const int Busy = 75;

    internal virtual async Task<string> RunAsync(string target, string command, Action<string>? line, CancellationToken ct)
    {
        var started = ProcessGroup.Leading(new AgentCommand("ssh",
            ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ControlMaster=no", "-o", "ControlPath=none", "--", target, command],
            Directory.GetCurrentDirectory()));
        return await RunProcessAsync(started, null, line, ct);
    }

    protected static async Task<string> RunProcessAsync(AgentCommand command, IReadOnlyDictionary<string, string>? environment, Action<string>? line, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command.FileName)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) start.Environment[key] = value;
        Process child;
        try { child = Process.Start(start) ?? throw new CallFailed("Remote backend command could not start.", "ssh_unavailable"); }
        catch (Win32Exception error) { throw new CallFailed($"Remote backend command could not start: {error.Message}", "ssh_unavailable"); }
        using var process = child;
        process.StandardInput.Close();
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var reading = ReadLines(process.StandardOutput, text => { lock (output) if (output.Length < ErrorLimit) output.AppendLine(text); line?.Invoke(text); });
        var failing = ReadLines(process.StandardError, text => { lock (errors) if (errors.Length < ErrorLimit) errors.AppendLine(text); });
        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(reading, failing).WaitAsync(ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                ProcessGroup.Kill(process.Id);
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(reading, failing);
        }
        if (process.ExitCode != 0)
        {
            var detail = string.Join('\n', new[] { output.ToString().Trim(), errors.ToString().Trim() }.Where(part => part.Length > 0));
            throw new CallFailed(detail.Length == 0 ? "Remote backend command failed." : $"Remote backend command failed: {detail}",
                process.ExitCode switch { 255 => "ssh_unavailable", Busy => "remote_busy", _ => "remote_command_failed" });
        }
        return output.ToString().Trim();
    }

    private static async Task ReadLines(StreamReader reader, Action<string> line)
    {
        while (await reader.ReadLineAsync() is { } text) line(text);
    }
}
