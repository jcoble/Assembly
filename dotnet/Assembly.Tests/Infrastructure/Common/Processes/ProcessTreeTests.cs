using System.Diagnostics;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Infrastructure.Common.Processes;

public sealed class ProcessTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assembly-stop-" + Guid.NewGuid().ToString("N"));
    public ProcessTreeTests() => Directory.CreateDirectory(_root);

    // A killed child of git passes to init, which clears it a moment later. A stopped run returns only after that, so
    // nothing it started is still listed. Without the wait, about half of these returned too early on Workbox.
    [Fact]
    public async Task Each_stopped_run_returns_once_what_git_started_is_gone()
    {
        for (var i = 0; i < 20; i++)
        {
            var pidFile = Path.Combine(_root, "owned-" + i);
            using var cancel = new CancellationTokenSource();
            using var watcher = new FileSystemWatcher(_root) { EnableRaisingEvents = true };
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Renamed += (_, e) => { if (e.FullPath == pidFile) ready.TrySetResult(); };
            var script = "!printf '%s' \"$$\" > '" + pidFile + ".partial'; mv '" + pidFile + ".partial' '" + pidFile + "'; exec sleep 60";
            var run = new GitCli().RunAsync(_root, ["-c", "alias.owned=" + script, "owned"], null, null, cancel.Token);
            if (File.Exists(pidFile)) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.False(Running(pid), $"run {i} returned while its child {pid} was still listed");
        }
    }

    private static bool Running(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
