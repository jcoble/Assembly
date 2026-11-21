using Assembly.Application.Common.Calls;
using Assembly.Application.Common.GitHub;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GhCliTests
{
    // Pins: g9#12
    [Fact]
    public async Task Output_past_the_cap_is_refused()
    {
        using var executable = new Executable("head -c 2000001 /dev/zero\n");
        var failure = await Assert.ThrowsAsync<CallFailed>(() => new GhCli(executable.Path).RunAsync(executable.Root, [], null, TimeSpan.FromSeconds(10), default));
        Assert.Equal("GitHub's answer was too large", failure.Message);
    }

    // Pins: g9#23. The marker is produced by the actual child, not a delay.
    [Fact]
    public async Task Cancellation_after_process_start_is_distinct_from_prestart_cancellation()
    {
        using var executable = new Executable(UntilHostGone);
        using var watcher = new FileSystemWatcher(executable.Root, "ready");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => ready.TrySetResult();
        watcher.EnableRaisingEvents = true;
        using var cancel = new CancellationTokenSource();
        var running = new GhCli(executable.Path).RunAsync(executable.Root, [], null, TimeSpan.FromSeconds(30), cancel.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAsync<GhInterrupted>(() => running);
        using var before = new CancellationTokenSource(); before.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GhCli(executable.Path).RunAsync(executable.Root, [], null, TimeSpan.FromSeconds(30), before.Token));
        Assert.IsNotType<GhInterrupted>(failure);
    }

    /// <summary>A gh that runs until it's stopped: it says it's ready, then waits as long as whoever started it is
    /// alive, so a test host killed mid-test can't leave it behind.</summary>
    private const string UntilHostGone = "printf ready > ready\nwhile kill -0 $PPID 2>/dev/null; do sleep 0.1; done\nprintf gone > gone\n";

    private sealed class Executable : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("assembly-gh-").FullName;
        internal string Path { get; }
        internal Executable(string script)
        {
            Path = System.IO.Path.Combine(Root, "fixture-gh");
            File.WriteAllText(Path, "#!/bin/sh\n" + script);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
