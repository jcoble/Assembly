using System.Diagnostics;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Common.Acp;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Tests.Infrastructure.Agents;

/// <summary>A fake adapter whose fake Claude Code starts a child, as the real one starts its tools.</summary>
public sealed class AgentProcessTests : IDisposable
{
    private readonly string _childPidFile = Path.GetTempFileName();
    private readonly string _claudeScript = Path.GetTempFileName();

    [Fact]
    public async Task Killing_only_the_adapter_also_ends_what_it_started()
    {
        await using var adapter = Start();
        var child = await ChildAsync();

        // The adapter's pid alone, as a crash or kill -9 would end it.
        Process.GetProcessById(adapter.Id).Kill(entireProcessTree: false);
        await GoneAsync(child);
    }

    [Fact]
    public async Task Stopping_the_adapter_ends_what_it_started()
    {
        var adapter = Start();
        var child = await ChildAsync();

        await adapter.DisposeAsync();
        await GoneAsync(child);
    }

    [Fact]
    public async Task Killing_only_the_adapter_also_ends_a_child_in_a_session_of_its_own()
    {
        // Claude Code starts its Bash tool's shell with setsid, outside the adapter's group.
        // It writes its own pid once it has left, so the kill can't catch it still inside.
        await using var adapter = Start(
            $"perl -MPOSIX -e 'POSIX::setsid(); open my $f, q(>), $ARGV[0]; print $f $$; close $f; exec q(sleep), 300' '{_childPidFile}' & wait");
        var child = await ChildAsync();

        Process.GetProcessById(adapter.Id).Kill(entireProcessTree: false);
        await GoneAsync(child);
    }

    /// <summary>
    /// The fake adapter starts a fake Claude Code (a shell in its group) that runs <paramref name="claude"/>, which
    /// starts a child and writes its pid, as the real one starts its tools.
    /// </summary>
    private AgentProcess Start(string? claude = null)
    {
        File.WriteAllText(_claudeScript, (claude ?? $"sleep 300 & echo $! > '{_childPidFile}'; wait") + "\n");
        return AgentProcess.Start(
            new AgentCommand("/bin/sh", ["-c", "/bin/sh \"$1\" & wait", "fake-adapter", _claudeScript], Path.GetTempPath()),
            TranscriptLog.Discard());
    }

    private async Task<int> ChildAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!int.TryParse(File.ReadAllText(_childPidFile).Trim(), out var pid))
        {
            Assert.True(DateTime.UtcNow < deadline, "the fake adapter didn't start its child");
            await Task.Delay(10);
        }

        return int.Parse(File.ReadAllText(_childPidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task GoneAsync(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Running(pid))
        {
            Assert.True(DateTime.UtcNow < deadline, $"pid {pid} outlived its adapter");
            await Task.Delay(20);
        }
    }

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
        if (int.TryParse(File.ReadAllText(_childPidFile).Trim(), out var pid) && Running(pid))
            Process.GetProcessById(pid).Kill();
        File.Delete(_childPidFile);
        File.Delete(_claudeScript);
    }
}
