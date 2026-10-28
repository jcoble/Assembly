using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Assembly.Infrastructure.Remote;

using Assembly.Tests.Features.Diagnostics;
using Assembly.Tests.Infrastructure.Http;
namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>The tunnel's process handling, with sleep and a failing shell standing in for ssh.</summary>
public sealed class RemoteTunnelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task It_runs_in_its_own_process_group_and_is_stopped_by_pid()
    {
        // The command leads its own group, then makes a file named by its pid in this folder, then becomes sleep.
        var folder = Directory.CreateTempSubdirectory("assembly-tunnel-").FullName;
        using var watcher = new FileSystemWatcher(folder) { EnableRaisingEvents = true };
        var made = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, file) => made.TrySetResult(file.Name!);
        var tunnel = new RemoteTunnel("/usr/bin/perl",
            ["-e", """setpgrp(0, 0); open(my $f, ">", "$ARGV[0]/$$") or die; close($f); shift; exec @ARGV or die""", folder, "/bin/sleep", "30"],
            TextWriter.Null);
        var pid = int.Parse(await made.Task.WaitAsync(Wait), CultureInfo.InvariantCulture);
        Assert.Equal(pid, getpgid(pid));
        // Taken while it runs, so its exit can be waited on.
        using var process = Process.GetProcessById(pid);

        await tunnel.DisposeAsync();

        await process.WaitForExitAsync().WaitAsync(Wait);
        Assert.NotEqual(0, kill(pid, 0));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task It_starts_again_when_the_command_exits()
    {
        var log = new SharedLog();
        await using var tunnel = new RemoteTunnel("/bin/sh", ["-c", "echo gone >&2; exit 3"], log);

        // Logged as the second start's command exits.
        await log.WrittenAsync("tunnel: ssh exited (3: gone); again in 2 s");

        Assert.True(tunnel.Starts >= 2);
        Assert.Contains("tunnel: ssh exited (3: gone); again in 1 s", log.ToString());
    }

    [Fact]
    public async Task It_says_when_the_command_exits()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var tunnel = new RemoteTunnel("/bin/sh", ["-c", "exit 3"], TextWriter.Null);
        tunnel.Exited += () => exited.TrySetResult();

        // The first exit may come before the handler; the next start's won't.
        await exited.Task.WaitAsync(Wait);
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

        await log.WrittenAsync("tunnel: ssh exited");

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

    [DllImport("libc")]
    private static extern int getpgid(int pid);

    [DllImport("libc")]
    private static extern int kill(int pid, int signal);
}
