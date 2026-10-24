using System.Diagnostics;
using Assembly.Engine.Transcript;

namespace Assembly.Engine.Agents;

/// <summary>
/// One running adapter process. It's disposable on purpose: when a session goes quiet the backend
/// stops it, and the next send starts a new one. It leads its own process group, and the group is killed whenever
/// the adapter ends, for any reason, so Claude Code never outlives it.
/// </summary>
public sealed class AgentProcess : IAsyncDisposable
{
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);

    private readonly Process _process;
    private readonly Task _stderrPump;

    private AgentProcess(Process process, TranscriptLog transcript)
    {
        _process = process;
        Id = process.Id;
        _stderrPump = PumpStderrAsync(process.StandardError, transcript);
        Exited = WatchExitAsync(transcript);
    }

    public static AgentProcess Start(AgentCommand command, TranscriptLog transcript)
    {
        command = ProcessGroup.Leading(command);
        var start = new ProcessStartInfo(command.FileName)
        {
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in command.Environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;

        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {command.FileName}.");
        return new AgentProcess(process, transcript);
    }

    public int Id { get; }

    /// <summary>Completes when the process ends, for any reason. Its exit is logged to the transcript.</summary>
    public Task Exited { get; }

    /// <summary>What we write to (the agent's stdin).</summary>
    public Stream Input => _process.StandardInput.BaseStream;

    /// <summary>What we read from (the agent's stdout).</summary>
    public Stream Output => _process.StandardOutput.BaseStream;

    /// <summary>Ends the adapter now, without the grace period. Used when an adapter is stuck, and to record a lost run.</summary>
    public void Kill() => ProcessGroup.Kill(Id);

    private async Task WatchExitAsync(TranscriptLog transcript)
    {
        await _process.WaitForExitAsync();
        // What it started goes with it, and their hold on its stderr with them.
        ProcessGroup.Kill(Id);
        transcript.AgentExited(_process.ExitCode);
    }

    private static async Task PumpStderrAsync(StreamReader stderr, TranscriptLog transcript)
    {
        try
        {
            while (await stderr.ReadLineAsync() is { } line)
                transcript.AgentStderr(line);
        }
        catch (Exception)
        {
            // The process is gone; stderr goes with it.
        }
    }

    /// <summary>Closes stdin so the adapter can exit on its own, then kills its group if it doesn't.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        using var grace = new CancellationTokenSource(ExitGrace);
        try
        {
            await _process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            // Only our own child's group, by its pid. Never by name.
            ProcessGroup.Kill(Id);
            await _process.WaitForExitAsync();
        }

        await _stderrPump;
        await Exited;
        _process.Dispose();
    }
}
