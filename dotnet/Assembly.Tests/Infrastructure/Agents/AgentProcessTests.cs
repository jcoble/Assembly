using System.Diagnostics;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Common.Acp;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Tests.Infrastructure.Agents;

/// <summary>A fake adapter whose fake Claude Code starts a child, as the real one starts its tools.</summary>
public sealed class AgentProcessTests : IDisposable
{
    // The child writes its pid down this pipe and holds it open, so the pipe's end is the child's end, zombie or not.
    private readonly string _childPipe = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _claudeScript = Path.GetTempFileName();
    private StreamReader? _child;
    private int? _childPid;

    [Fact]
    public async Task Killing_only_the_adapter_also_ends_what_it_started()
    {
        await using var adapter = Start();
        await ChildAsync();

        // The adapter's pid alone, as a crash or kill -9 would end it.
        Process.GetProcessById(adapter.Id).Kill(entireProcessTree: false);
        await GoneAsync();
    }

    [Fact]
    public async Task Stopping_the_adapter_ends_what_it_started()
    {
        var adapter = Start();
        await ChildAsync();

        await adapter.DisposeAsync();
        await GoneAsync();
    }

    [Fact]
    public async Task Killing_only_the_adapter_also_ends_a_child_in_a_session_of_its_own()
    {
        // Claude Code starts its Bash tool's shell with setsid, outside the adapter's group.
        // It writes its own pid once it has left, so the kill can't catch it still inside.
        await using var adapter = Start("perl -MPOSIX -e 'POSIX::setsid(); $| = 1; print qq($$\n); exec q(sleep), 300' >&3 & wait");
        await ChildAsync();

        Process.GetProcessById(adapter.Id).Kill(entireProcessTree: false);
        await GoneAsync();
    }

    /// <summary>
    /// The fake adapter starts a fake Claude Code (a shell in its group) that runs <paramref name="claude"/>, which
    /// starts a child and writes its pid to the pipe on fd 3, as the real one starts its tools.
    /// </summary>
    private AgentProcess Start(string? claude = null)
    {
        using (var mkfifo = Process.Start("/usr/bin/mkfifo", [_childPipe]))
            mkfifo.WaitForExit();
        File.WriteAllText(_claudeScript, $"exec 3>'{_childPipe}'\n" + (claude ?? "sleep 300 & echo $! >&3; wait") + "\n");
        return AgentProcess.Start(
            new AgentCommand("/bin/sh", ["-c", "/bin/sh \"$1\" & wait", "fake-adapter", _claudeScript], Path.GetTempPath()),
            TranscriptLog.Discard());
    }

    private async Task ChildAsync()
    {
        // Opening a pipe waits for its writer.
        _child = new StreamReader(await Task.Run(() => File.OpenRead(_childPipe)).WaitAsync(TimeSpan.FromSeconds(5)));
        _childPid = int.Parse((await _child.ReadLineAsync())!, System.Globalization.CultureInfo.InvariantCulture);
    }

    // The pipe ends once every process holding it has ended, the child among them.
    private Task GoneAsync() => _child!.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(2));

    /// <summary>
    /// Alive and not a zombie. A killed child of an orphaned parent stays a zombie until init reaps it, which can
    /// be slow as PID 1 in a container; it's gone all the same.
    /// </summary>
    private static bool Running(int pid)
    {
        var start = new ProcessStartInfo("/bin/ps", ["-o", "stat=", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var ps = Process.Start(start)!;
        var state = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        return state.Length > 0 && !state.StartsWith('Z');
    }

    public void Dispose()
    {
        // If a test failed, don't leave its child behind. Only that pid, which this test started.
        if (_childPid is { } pid && Running(pid))
            Process.GetProcessById(pid).Kill();
        _child?.Dispose();
        File.Delete(_childPipe);
        File.Delete(_claudeScript);
    }
}
