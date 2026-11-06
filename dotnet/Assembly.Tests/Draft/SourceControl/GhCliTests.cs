using Assembly.Application.Common.Calls;
using Assembly.Application.Common.GitHub;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GhCliTests
{
    // Pins: g9#12
    [Fact]
    public async Task A_missing_gh_says_how_to_install()
    {
        using var executable = new Executable("exit 0\n");
        var missing = new GhCli(Path.Combine(executable.Root, "missing"));
        var failure = await Assert.ThrowsAsync<CallFailed>(() => missing.RunAsync(executable.Root, ["api"], null, TimeSpan.FromSeconds(10), default));
        Assert.Contains("GitHub CLI (gh) isn't installed", failure.Message);
        Assert.Contains("gh auth login", failure.Message);
    }

    // Pins: g9#12
    [Fact]
    public async Task Json_stdin_arguments_and_noninteractive_environment_reach_gh_exactly()
    {
        using var executable = new Executable("printf '%s\\n' \"$GH_PROMPT_DISABLED|$NO_COLOR|$GH_NO_UPDATE_NOTIFIER\"\nprintf '%s\\n' \"$1\" \"$2\"\ncat\n");
        const string input = "{\"text\":\"quote \\\" and λ\\nline\"}";
        var result = await new GhCli(executable.Path).RunAsync(executable.Root, ["api", "literal;argument"], input, TimeSpan.FromSeconds(10), default);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1|1|1\napi\nliteral;argument\n" + input, result.Output);
    }

    // Pins: g9#12
    [Fact]
    public async Task Output_past_the_cap_is_refused()
    {
        using var executable = new Executable("head -c 2000001 /dev/zero\n");
        var failure = await Assert.ThrowsAsync<CallFailed>(() => new GhCli(executable.Path).RunAsync(executable.Root, [], null, TimeSpan.FromSeconds(10), default));
        Assert.Equal("GitHub's answer was too large", failure.Message);
    }

    // Pins: g9#12
    [Fact]
    public async Task Signed_out_gh_keeps_its_exit_code_and_text()
    {
        using var executable = new Executable("printf 'Run gh auth login' >&2\nexit 4\n");
        var result = await new GhCli(executable.Path).RunAsync(executable.Root, ["pr", "list"], null, TimeSpan.FromSeconds(10), default);
        Assert.Equal(4, result.ExitCode); Assert.Equal("Run gh auth login", result.Error);
    }

    // Pins: g9#20
    [Fact]
    public async Task Raw_stdout_retains_non_utf8_bytes_and_distinguishes_an_empty_response()
    {
        using var executable = new Executable("printf '\\377\\000'\n");
        var result = await new GhCli(executable.Path).RunAsync(executable.Root, [], null, TimeSpan.FromSeconds(10), default);
        Assert.True(result.OutputBytes.HasValue);
        Assert.Equal(new byte[] { 255, 0 }, result.OutputBytes.Value.ToArray());
        using var empty = new Executable("exit 0\n");
        var none = await new GhCli(empty.Path).RunAsync(empty.Root, [], null, TimeSpan.FromSeconds(10), default);
        Assert.True(none.OutputBytes.HasValue); Assert.Empty(none.OutputBytes.Value.ToArray());
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

    [Fact]
    public async Task A_waiting_fixture_ends_once_whoever_started_it_is_gone()
    {
        using var executable = new Executable(UntilHostGone);
        using var watcher = new FileSystemWatcher(executable.Root, "gone");
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => gone.TrySetResult();
        watcher.EnableRaisingEvents = true;
        using var host = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", $"'{executable.Path}' &"])
        { WorkingDirectory = executable.Root })!;
        await host.WaitForExitAsync();
        await gone.Task.WaitAsync(TimeSpan.FromSeconds(10)); // throws if the fixture still runs with its host gone
    }

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
