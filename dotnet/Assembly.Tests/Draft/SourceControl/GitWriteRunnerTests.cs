using System.Diagnostics;
using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitWriteRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assembly-write-" + Guid.NewGuid().ToString("N"));
    public GitWriteRunnerTests() => Directory.CreateDirectory(_root);

    // Pins: g9#2; Task1 exact stdin, all exit codes.
    [Fact]
    public async Task Stdin_has_no_added_nul_and_exit_one_is_retained()
    {
        var git = new GitCli();
        var hashed = await git.RunAsync(_root, ["hash-object", "--stdin"], "a\n", null, default);
        Assert.Equal(0, hashed.ExitCode);
        Assert.Equal("78981922613b2afb6025042ff6bd878ac1994e85\n", hashed.Output);
        Assert.Equal(0, (await git.RunAsync(_root, ["init"], null, null, default)).ExitCode);
        var empty = await git.RunAsync(_root, ["-c", "user.name=Git Test", "-c", "user.email=git@example.invalid", "commit", "-m", "empty"], null, null, default);
        Assert.Equal(1, empty.ExitCode);
        Assert.Contains("nothing to commit", empty.Output + empty.Error, StringComparison.OrdinalIgnoreCase);
    }

    // Pins: g9#10; bounded write results keep complete UTF8 characters.
    [Fact]
    public async Task Large_write_output_is_drained_and_capped_without_a_broken_unicode_tail()
    {
        var git = new GitCli();
        await git.RunAsync(_root, ["init"], null, null, default);
        var text = new string('界', 50000);
        var hash = await git.RunAsync(_root, ["hash-object", "-w", "--stdin"], text, null, default);
        var run = await git.RunAsync(_root, ["cat-file", "-p", hash.Output.Trim()], null, null, default);
        Assert.Equal(0, run.ExitCode);
        Assert.EndsWith("…", run.Output);
        Assert.True(Encoding.UTF8.GetByteCount(run.Output[..^1]) <= 64 * 1024);
        Assert.Equal(text[..(run.Output.Length - 1)], run.Output[..^1]);
    }

    // Pins: g9#27; only the exact owned process tree is stopped and joined.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_cancel_and_safety_deadline_stop_the_owned_process(bool deadline)
    {
        var time = new FakeTimeProvider();
        var git = new GitCli(time);
        using var cancel = new CancellationTokenSource();
        var pidFile = Path.Combine(_root, "owned-pid");
        using var watcher = new FileSystemWatcher(_root) { EnableRaisingEvents = true };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Renamed += (_, e) => { if (e.FullPath == pidFile) ready.TrySetResult(); };
        var script = "!printf '%s' \"$$\" > '" + pidFile + ".partial'; mv '" + pidFile + ".partial' '" + pidFile + "'; exec sleep 60";
        var run = git.RunAsync(_root, ["-c", "alias.owned=" + script, "owned"], null, TimeSpan.FromSeconds(60), cancel.Token);
        try
        {
            if (File.Exists(pidFile)) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            if (deadline) time.Advance(TimeSpan.FromSeconds(60));
            else await cancel.CancelAsync();
            if (deadline)
                Assert.Contains("longer than 60 seconds", (await Assert.ThrowsAsync<CallFailed>(() => run)).Message);
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
            catch (ArgumentException) { } // Already reaped after the runner joined its exact child.
        }
        finally
        {
            await cancel.CancelAsync();
            try { await run; } catch (Exception error) when (error is CallFailed or OperationCanceledException) { }
        }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
