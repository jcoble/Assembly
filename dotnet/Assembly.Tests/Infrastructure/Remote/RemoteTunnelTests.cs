using System.Runtime.InteropServices;
using Assembly.Infrastructure.Remote;

using Assembly.Tests.Features.Diagnostics;
using Assembly.Tests.Infrastructure.Http;
namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>The tunnel's process handling, with sleep and a failing shell standing in for ssh.</summary>
public sealed class RemoteTunnelTests
{
    [Fact]
    public async Task It_runs_in_its_own_process_group_and_is_stopped_by_pid()
    {
        var tunnel = new RemoteTunnel("/usr/bin/perl", ["-e", "setpgrp(0, 0); exec @ARGV or die", "/bin/sleep", "30"], TextWriter.Null);
        await Until(() => tunnel.Pid > 0);
        var pid = tunnel.Pid;
        await Until(() => getpgid(pid) == pid);

        await tunnel.DisposeAsync();

        await Until(() => kill(pid, 0) != 0);
    }

    [Fact]
    public async Task It_starts_again_when_the_command_exits()
    {
        var log = new SharedLog();
        await using var tunnel = new RemoteTunnel("/bin/sh", ["-c", "echo gone >&2; exit 3"], log);

        await Until(() => tunnel.Starts >= 2);

        Assert.Contains("tunnel: ssh exited (3: gone); again in 1 s", log.ToString());
    }

    [Fact]
    public async Task It_says_when_the_command_exits()
    {
        var exits = 0;
        await using var tunnel = new RemoteTunnel("/bin/sh", ["-c", "exit 3"], TextWriter.Null);
        tunnel.Exited += () => Interlocked.Increment(ref exits);

        // The first exit may come before the handler; the next start's won't.
        await Until(() => Volatile.Read(ref exits) >= 1);
    }

    // ssh mustn't hold this process's stdout or stdin: after a hard exit here, an orphaned ssh would keep the window's
    // launcher from seeing the end of this process's output.
    [LinuxFact]
    public async Task The_command_has_its_own_stdout_and_stdin()
    {
        var log = new SharedLog();
        var here = $"/proc/{Environment.ProcessId}/fd";
        await using var tunnel = new RemoteTunnel("/bin/sh",
            ["-c", $"""[ "$(readlink /proc/$$/fd/0)" != "$(readlink {here}/0)" ] && [ "$(readlink /proc/$$/fd/1)" != "$(readlink {here}/1)" ] && echo own >&2 || echo shared >&2; exit 3"""],
            log);

        await Until(() => log.ToString().Contains("tunnel: ssh exited"));

        Assert.Contains("tunnel: ssh exited (3: own)", log.ToString());
    }

    // It reads /proc, which macOS doesn't have.
    private sealed class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute()
        {
            if (!OperatingSystem.IsLinux())
                Skip = "Linux only";
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    [DllImport("libc")]
    private static extern int getpgid(int pid);

    [DllImport("libc")]
    private static extern int kill(int pid, int signal);
}
